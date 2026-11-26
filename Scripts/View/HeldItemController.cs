using System;
using Godot;
using SaleFish.Inventory;

namespace SaleFish.View;

/// <summary>
/// 全局「手」（autoload 单例，见 project.godot 的 HeldItemController 段）：
/// 全游戏唯一的手持状态与输入入口，取代原先每仓库一个的 InventoryInputController。
/// 自带手持渲染层（HandRenderLayer，挂在高图层 CanvasLayer）：幽灵预览 + 物品本体永远画在各仓库之上。
///
/// 设计要点：
///  ▪ 手持 = 全局事实：任何时刻至多一个 HeldItem，所有仓库共用这一只手，不再有「谁在拿」的归属问题；
///  ▪ 坐标存全局空间：HeldVisPos 与取放都按全局坐标计算，各视图/手持层减掉自己的
///    GetGlobalRect().Position 或直接用（手持层无偏移），此前跨仓 bug 的根源正是坐标空间不统一；
///  ▪ 溯源（provenance）：拿起时记录「来源仓库 + 原锚点 + 拿起时旋转」——仓库 Hide 时据此
///    把物品放回原位（先原位 → 扫第一个合法空位 → 实在没空位保持手持）；Destroy 时清溯源、物品留手；
///  ▪ 输入入口：仓库视图把点击/旋转请求转发进来（OnLeftPressed(view, local) / OnRightPressed()），
///    拿起/放下/跨仓落位都在这里统一处理，视图层不再需要跨仓分支。
/// </summary>
public sealed partial class HeldItemController : Node
{
    /// <summary>默认抓取格大小：生成物品且指针下没有仓库可参考时使用（主测试视图 52px）。</summary>
    public const int DefaultCellSize = 52;

    /// <summary>autoload 注入的全局实例（project.godot 里挂的 HeldItemController 节点）。</summary>
    public static HeldItemController Instance { get; private set; } = null!;

    public override void _Ready()
    {
        Instance = this;

        // 手持渲染层：挂在高图层（CanvasLayer）上的全屏 Control，永远画在所有仓库之上。
        // 由「手」自行创建，保证不依赖某个具体仓库/场景存在。
        var layer = new CanvasLayer { Layer = HandRenderOrder };
        AddChild(layer);
        var renderer = new HandRenderLayer();
        layer.AddChild(renderer);
        renderer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        renderer.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    /// <summary>手持渲染层的图层序：高于默认画布（0），手持本体/幽灵预览永不被仓库或工具栏遮挡。</summary>
    private const int HandRenderOrder = 64;

    /// <summary>当前手持物品；null 表示空手。</summary>
    public InventoryItem? HeldItem { get; private set; }

    /// <summary>抓手格：形状内相对坐标，鼠标按住的那一格。保证拿起/旋转时不跳变。</summary>
    public Vector2I GrabCell { get; private set; }

    /// <summary>手持物品的视觉锚点（物品左上角所在点，全局坐标），每帧直接对齐「鼠标 - 抓取偏移」。</summary>
    public Vector2 HeldVisPos { get; private set; }

    /// <summary>鼠标与物品视觉锚点之间的像素偏移（拿起那一瞬间固定下来）。</summary>
    private Vector2 _grabPixelOffset;

    /// <summary>这次拿起的格大小：抓取偏移/抓取格都以它为基准（拿起的仓库与落点仓库可能格大小不同；
    /// 手持本体也按它渲染，跨仓库时物品视觉尺度不跳变）。</summary>
    private int _grabCellSize = DefaultCellSize;

    /// <summary>抓取像素偏移（公开给渲染与测试，视觉跟随的基准）。</summary>
    public Vector2 GrabVisualOffset => _grabPixelOffset;

    /// <summary>拿起/生成时的格大小（HandRenderLayer 绘制手持本体用；见 _grabCellSize 注释）。</summary>
    public int GrabCellSize => _grabCellSize;

    /// <summary>
    /// 锚点在抓手格内的 0..1 比例（中心=0.5，边中点/角=0 或 1）。
    /// 由抓取偏移与抓手格直接推导（不加新状态）：放置/预览用它做子格精度悬停检测（见 InventoryView.ToSubCell）。
    /// </summary>
    public Vector2 GrabSnapFraction =>
        HeldItem == null
            ? new Vector2(0.5f, 0.5f)
            : (_grabPixelOffset - (Vector2)(GrabCell * _grabCellSize)) / _grabCellSize;

    // ---- 旋转动画：开启时右键旋转播放约 RotateAnimationDuration 秒动画，动画中禁止再旋转/放置 ----
    public bool EnableRotateAnimation { get; set; } = true;
    public double RotateAnimationDuration { get; set; } = 0.2;

    private bool _animActive;
    private float _animElapsed;
    private PlacementShape _animFromShape = null!;
    private Vector2 _animFromGrabOffset;

    /// <summary>旋转动画进行中（此时禁止旋转与放置）。</summary>
    public bool IsRotateAnimating => _animActive;

    /// <summary>旋转动画起点形状（旋转前的形状，供视图绘制转动过程）。</summary>
    public PlacementShape RotateFromShape => _animFromShape;

    /// <summary>旋转动画起点的抓手像素偏移（旋转前锚点相对物品左上角的位置，视图据此算绕锚点的轨道）。</summary>
    public Vector2 RotateFromGrabOffset => _animFromGrabOffset;

    /// <summary>旋转动画已推进的时间（秒）。</summary>
    public float RotateAnimElapsed => _animElapsed;

    /// <summary>手持状态变化时触发（携带新的手持物品或 null）。全局唯一事件源——状态栏等只需订阅这一个。</summary>
    public event Action<InventoryItem?>? HeldChanged;

    // ---- 溯源：当前手持物品是从哪个仓库拿走的（生成/无来源物品为 null） ----
    private InventoryView? _sourceView;
    private Vector2I _returnAnchor;        // 拿起时的原锚点
    private int _returnRotationTurns;      // 拿起时的旋转次数（隐藏还原时连旋转一起复原）

    public override void _Process(double delta)
    {
        TickRotateAnimation(delta);
        if (HeldItem == null) return;
        HeldVisPos = GlobalMouse() - _grabPixelOffset;
    }

    /// <summary>当前全局鼠标位置（画布坐标）：优先经任一在用仓库视图取（GetGlobalMousePosition 只在
    /// CanvasItem 上），无任何仓库时回退 Viewport.GetMousePosition（本游戏无相机/画布变换，两者等价）。</summary>
    private Vector2 GlobalMouse()
    {
        var view = WarehouseRegistry.LastView();
        return view != null ? view.GetGlobalMousePosition() : GetViewport().GetMousePosition();
    }

    /// <summary>推进旋转动画进度；时间参数化以便自动化测试注入。到点后自动复位，恢复旋转/放置。</summary>
    public void TickRotateAnimation(double delta)
    {
        if (!_animActive) return;
        _animElapsed += (float)delta;
        if (_animElapsed >= (float)RotateAnimationDuration) _animActive = false;
    }

    /// <summary>
    /// 测试注入：直接把物品锚点对齐给定全局鼠标位置 - 拿起时固定的像素偏移，
    /// 不做任何平滑。鼠标位置参数化以便自动化测试注入（headless 下 OS 光标不可用）。
    /// </summary>
    public void AdvanceFollow(Vector2 mouseGlobal)
    {
        if (HeldItem == null) return;
        HeldVisPos = mouseGlobal - _grabPixelOffset;
    }

    /// <summary>视图转发来的左键点击（视图本地坐标 + 视图本身）。拿起/放置/跨仓落位统一在此处理。</summary>
    public void OnLeftPressed(InventoryView view, Vector2 viewLocalPos)
    {
        if (EnableRotateAnimation && IsRotateAnimating) return; // 旋转动画中禁止放下/拿起

        if (HeldItem == null)
            PickUp(view, viewLocalPos);
        else
            TryPlace(view, viewLocalPos);
    }

    /// <summary>空手：点击物品即可拿起，并记录溯源（来源仓库/原锚点/拿起时旋转）。</summary>
    private void PickUp(InventoryView view, Vector2 viewLocalPos)
    {
        var cell = view.ToCell(viewLocalPos);
        var item = view.Inventory.GetItemAt(cell);
        if (item == null || !view.Inventory.RemoveItem(item)) return;

        HeldItem = item;
        GrabCell = cell - item.Origin;
        // 抓取点吸附：把点击落点映射到所在格可用锚点中最近的一个（中心恒可用；
        // 边中点需为形状内格子共用边，角需 ≥3 格共用），后续跟随与旋转的中心都以该锚点为基准。
        var snap = SnapInCell(viewLocalPos - view.CellToPixel(cell), view.CellSize, item.Shape, GrabCell);
        _grabCellSize = view.CellSize;
        _grabPixelOffset = (Vector2)(GrabCell * _grabCellSize) + snap;
        // 拿起瞬间视觉零跳变：物品仍画在原来所在位置（全局坐标），此后跟随鼠标。
        HeldVisPos = view.GetGlobalRect().Position + view.CellToPixel(item.Origin);

        _sourceView = view;
        _returnAnchor = item.Origin;
        _returnRotationTurns = item.RotationTurns;
        HeldChanged?.Invoke(HeldItem);
    }

    /// <summary>手持：点击网格尝试放置（合法放入 / 可交换则交换 / 否则保持手持）。落点容器 = 点击的视图。
    /// 跨仓库天然成立：手是全局的，落在哪个视图就放进哪个视图的容器。</summary>
    private void TryPlace(InventoryView view, Vector2 viewLocalPos)
    {
        var held = HeldItem;
        if (held == null) return;

        var anchor = view.ToSubCell(viewLocalPos, GrabSnapFraction) - GrabCell;
        var clickedGlobal = view.GetGlobalRect().Position + viewLocalPos;   // 交换顶起后顶起物居中于此
        var result = view.Inventory.TryPlace(held, anchor);
        switch (result.Outcome)
        {
            case PlaceOutcome.Success:
                HeldItem = null;
                _sourceView = null;
                break;

            case PlaceOutcome.Swap:
            {
                // 成功放下，原物品顶到手上：居中出现在点击位置。溯源改为顶起物（它来自 target 视图）。
                var bumped = result.Bumped!;
                HeldItem = bumped;
                GrabCell = bumped.Shape.Size / 2;
                _grabCellSize = view.CellSize;
                _grabPixelOffset = ((Vector2)(bumped.Shape.Size * _grabCellSize)) / 2f;
                HeldVisPos = clickedGlobal - _grabPixelOffset;
                _sourceView = view;
                _returnAnchor = bumped.Origin;   // 它在 target 里的原位（若隐藏 target，先试放回这里）
                _returnRotationTurns = bumped.RotationTurns;
                break;
            }

            default:
                return; // 无法放置，保持手持，什么都不做
        }
        HeldChanged?.Invoke(HeldItem);
    }

    /// <summary>视图转发来的右键点击：仅手持时可旋转（不管按在哪，旋转作用于手持物品本身）。</summary>
    public void OnRightPressed()
    {
        if (HeldItem == null) return;
        if (EnableRotateAnimation && IsRotateAnimating) return; // 动画进行中禁止再次旋转

        var oldShape = HeldItem.Shape;
        var oldGrab = GrabCell;
        var oldOffset = _grabPixelOffset;

        HeldItem.RotateClockwise();

        // 旋转中心 = 被拿住的那格：把抓手格随形一起变换，而不是重置回形状中心。
        // 顺时针旋转下，旧格 (gx,gy)（旧高度 H）→ 新格 (H-1-gy, gx)。
        GrabCell = new Vector2I(oldShape.Height - 1 - oldGrab.Y, oldGrab.X);

        // 视觉：抓取点（点击的那一点）在格内的相对位置也跟着旋转，
        // 让被抓住的格子始终钉在鼠标下 → 旋转中心即点击位置，全程零跳变。
        var cell = (float)_grabCellSize;
        var offInCell = oldOffset - (Vector2)(oldGrab * _grabCellSize);   // 抓取点在旧格内的偏移
        var rotatedOff = new Vector2(cell - offInCell.Y, offInCell.X);    // 格子旋转 90° 后偏移随之旋转
        _grabPixelOffset = (Vector2)(GrabCell * _grabCellSize) + rotatedOff;

        if (EnableRotateAnimation)
        {
            // 记录旋转前快照（形状与锚点偏移），供视图绕「当前锚点」播放 0→90° 的转动动画：
            // 形状已即时切换，绘制是视觉叠加；锚点每帧取 HeldVisPos + GrabVisualOffset = 当前鼠标。
            _animFromShape = oldShape;
            _animFromGrabOffset = oldOffset;
            _animElapsed = 0f;
            _animActive = true;
        }

        HeldChanged?.Invoke(HeldItem);
    }

    /// <summary>外部把物品交给鼠标（生成按钮等）。anchorView 提供参考：初始抓手锚点与跟随的格大小；
    /// cellSize 显式指定格大小（通常=目标仓库配置），优先级最高；两者均缺省回退默认 52
    /// （生成物品与任何仓库无关，隐藏不会还原它）。</summary>
    public void TakeExternal(InventoryItem item, InventoryView? anchorView = null, int? cellSize = null)
    {
        HeldItem = item;
        _grabCellSize = cellSize ?? anchorView?.CellSize ?? DefaultCellSize;
        var anchor = ResolveInitialAnchor(item, _grabCellSize);
        if (anchor is (var cell, var frac))
        {
            GrabCell = cell;
            _grabPixelOffset = (Vector2)(cell * _grabCellSize) + frac * _grabCellSize;
        }
        else
        {
            GrabCell = item.Shape.Size / 2;
            _grabPixelOffset = ((Vector2)(item.Shape.Size * _grabCellSize)) / 2f;
        }
        HeldVisPos = GlobalMouse() - _grabPixelOffset;   // 抓取点钉在鼠标下

        _sourceView = null;
        _returnAnchor = default;
        _returnRotationTurns = 0;
        HeldChanged?.Invoke(HeldItem);
    }

    /// <summary>丢弃手持物品；空手时无效果。丢弃会同时取消进行中的旋转动画、清空溯源。</summary>
    public void DiscardHeld()
    {
        _animActive = false;
        if (HeldItem == null) return;
        HeldItem = null;
        _sourceView = null;
        _returnAnchor = default;
        _returnRotationTurns = 0;
        HeldChanged?.Invoke(HeldItem);
    }

    /// <summary>
    /// 仓库隐藏回调用（InventoryView.Hide）：若当前手持物品正是从它拿走的，放回原位——
    /// 先试原锚点；原位已被占（跨仓交换顶起的边角）则扫第一个合法空位；实在放不下保持手持、保留溯源
    /// （等仓库 Show 后再次隐藏还能再试）。放回时连旋转一起复原到拿起时状态。返回 true 表示已还原。
    /// </summary>
    public bool TryReturnToSource(InventoryView view)
    {
        if (HeldItem == null || _sourceView != view) return false;
        _animActive = false;   // 取消旋转动画再还原

        // 连旋转复原：顺时针转回到拿起时的朝向（形状/朝向随 RotateClockwise 一起变）。
        var back = (4 - ((HeldItem.RotationTurns - _returnRotationTurns) % 4)) % 4;
        for (var i = 0; i < back; i++) HeldItem.RotateClockwise();

        var space = view.Inventory;
        if (SpaceClearAt(space, HeldItem.Shape, _returnAnchor)
            && space.TryPlace(HeldItem, _returnAnchor).Outcome == PlaceOutcome.Success)
            return FinishReturn();

        // 原位不可用：扫第一个合法空位（行优先）。
        for (var y = 0; y < space.Size.Y; y++)
        for (var x = 0; x < space.Size.X; x++)
        {
            var a = new Vector2I(x, y);
            if (a == _returnAnchor) continue;   // 原位已试过
            if (!SpaceClearAt(space, HeldItem.Shape, a)) continue;
            if (space.TryPlace(HeldItem, a).Outcome == PlaceOutcome.Success)
                return FinishReturn();
        }

        return false;   // 实在放不下：保持手持、保留溯源
    }

    /// <summary>仓库销毁回调用（InventoryView.RemoveStorage）：它不再是当前手持物品的「家」。
    /// 物品保留在手中（按需求「销毁时手持物品保留」），溯源清除。</summary>
    public void ForgetSource(InventoryView view)
    {
        if (_sourceView == view)
        {
            _sourceView = null;
            _returnAnchor = default;
            _returnRotationTurns = 0;
        }
    }

    /// <summary>还原成功：物品已回到仓库，手清空、溯源清空。</summary>
    private bool FinishReturn()
    {
        HeldItem = null;
        _sourceView = null;
        HeldChanged?.Invoke(null);
        return true;
    }

    /// <summary>目标锚点的占格是否全部合法且无人占用（还原只做"空位归位"，不做交换）。</summary>
    private static bool SpaceClearAt(SpatialInventory space, PlacementShape shape, Vector2I anchor)
    {
        foreach (var c in shape.GetOccupiedCells())
            if (!space.IsCellValid(anchor + c) || space.GetItemAt(anchor + c) != null) return false;
        return true;
    }

    /// <summary>把点击点吸附到所在格可用锚点中最近的一个（过滤规则见 PlacementShape.GrabAnchorFractions），
    /// 返回格内像素偏移。</summary>
    public static Vector2 SnapInCell(Vector2 pInCell, float cellSize, PlacementShape shape, Vector2I grabCell)
    {
        var best = Vector2.Zero;
        var bestDist = float.MaxValue;
        foreach (var f in shape.GrabAnchorFractions(grabCell))
        {
            var anchor = f * cellSize;
            var d = (anchor - pInCell).LengthSquared();
            if (!(d < bestDist)) continue;
            bestDist = d;
            best = anchor;
        }
        return best;
    }

    /// <summary>
    /// 解析物品的初始抓取锚点（仅生成时使用）：Data 挂 PlacementItemTypeDef 且 InitialGrabCell 在形状范围内时，
    /// 返回 (抓手格, 合法的格内锚点比例)；未定义/越界返回 null（调用方回退默认居中）。
    /// 合法规则 = GrabAnchorFractions：中心恒可用；边中点需为形状内格子共用边；角需 ≥3 格共用。
    /// </summary>
    private static (Vector2I cell, Vector2 frac)? ResolveInitialAnchor(InventoryItem item, int cellSize)
    {
        if (item.Data is not PlacementItemTypeDef def) return null;
        var cell = def.InitialGrabCell;
        if (cell.X < 0 || cell.Y < 0) return null;   // 未定义
        if (cell.X >= item.Shape.Width || cell.Y >= item.Shape.Height) return null;   // 越界 → 回退居中

        var size = (float)cellSize;
        var snapped = SnapInCell(def.InitialSnap * size, size, item.Shape, cell);
        return (cell, snapped / size);
    }
}
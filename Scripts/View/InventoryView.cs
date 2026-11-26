using System;
using Godot;
using SaleFish.Common;
using SaleFish.Inventory;

namespace SaleFish.View;

/// <summary>
/// 渲染层：用 Godot 绘制把「数学状态的容器」呈现出来 —— 网格地面 + 已放置物品。
/// 输入逻辑由全局「手」（HeldItemController，autoload）统一处理：视图只负责把点击/旋转请求转发给它。
/// 手持态（幽灵预览 + 物品本体）由全局「手」自带的 HandRenderLayer 绘制（高图层，永远在各仓库之上），
/// 不在这里——仓库画布只画自己格子区内的东西，坐标用视图本地。
/// 底层绘制细节（色块/整图/投影/幽灵占格）都在 ItemRenderer（静态库），此处与 HandRenderLayer 共用。
/// </summary>
public sealed partial class InventoryView : Control
{
    /// <summary>单格像素尺寸。</summary>
    public int CellSize { get; private set; } = 52;

    /// <summary>网格左上角在视图本地坐标中的位置。</summary>
    public Vector2I GridOffset { get; private set; } = new(100, 92);

    public SpatialInventory Inventory { get; private set; } = null!;

    /// <summary>仓库外观配置（显示字段：单格/背景贴图等）；null 时用内置配色（当前效果）。</summary>
    private StorageTypeDef? _storageLook;

    /// <summary>已隐藏（Hide 后 Show 前）；确保 Hide/Show 可重复调用不串味。</summary>
    private bool _hidden;

    /// <summary>本仓库视图的所属按钮（创建它的 WarehouseButton）：宿主「外部点击关仓」据此定位到按钮再关，
    /// 保证按钮的生命周期/位置记忆/事件一致。测试/演示直接 CreateStorage 建的视图无按钮（null），
    /// 宿主遇到时退化为直接 Hide。</summary>
    public WarehouseButton? OwnerButton { get; set; }

    /// <summary>视图位置变化（空手拖拽整仓结束、与拖前不同）时触发。</summary>
    public event Action? Moved;

    // ---- 空手拖拽整座仓库（背景/空格子按住 → 面板跟随光标，见 _GuiInput 分流） ----
    private bool _dragActive;
    private Vector2 _dragGrabLocal;   // 按下位置（视图本地，视图原点 = 仓库左上角）
    private Vector2 _dragStartPos;    // 本次拖拽开始时的 Position（EndDrag 判定位置是否真变了）

    // 配色
    private static readonly Color CellFill = new("2b313a");

    public void Setup(SpatialInventory inventory, int cellSize = 52,
        Vector2I? gridOffset = null, StorageTypeDef? storageLook = null)
    {
        Inventory = inventory;
        CellSize = cellSize;
        if (gridOffset.HasValue) GridOffset = gridOffset.Value;
        _storageLook = storageLook;
        // 池化复用的视图可能曾被 Hide（不可见）后交回，重新挂载时要恢复到可见的初始状态。
        _hidden = false;
        Visible = true;
        // 接管落在网格上的鼠标事件（按钮绘制在更上层，仍优先于本视图）。
        MouseFilter = Control.MouseFilterEnum.Stop;
        WarehouseRegistry.Register(this);   // 进入在用仓库表：参与指针命中/顶层绘制判定
    }

    /// <summary>离场（被移除/回收进对象池）时注销登记，不再参与指针路由。</summary>
    public override void _ExitTree()
    {
        WarehouseRegistry.Unregister(this);
    }

    /// <summary>
    /// 隐藏仓库（如关闭背包）：视图不可见、退出指针路由；若「手」正拿着从它拿走的物品会放回原位
    /// （见 HeldItemController.TryReturnToSource）。数据与视图都保留（不进对象池），Show() 恢复。
    /// 隐藏不影响其它仓库的物品——只有从本仓库拿走的才受牵连。
    /// </summary>
    /// new：有意覆盖 CanvasItem.Hide()（那只是 Visible=false），本方法还负责还原手持与注销登记。
    public new void Hide()
    {
        if (_hidden) return;
        _hidden = true;
        HeldItemController.Instance?.TryReturnToSource(this);
        WarehouseRegistry.Unregister(this);
        Visible = false;
    }

    /// <summary>重新显示隐藏的仓库：恢复可见并重新登记（回到顶层）。</summary>
    /// new：有意覆盖 CanvasItem.Show()（那只是 Visible=true），本方法还负责恢复登记。
    public new void Show()
    {
        if (!_hidden) return;
        _hidden = false;
        Visible = true;
        WarehouseRegistry.Register(this);
    }

    /// <summary>
    /// 在 parent 下、position（仓库网格左上角）处创建一整套可直接交互的仓库：
    /// 数据容器（SpatialInventory）+ 视图（InventoryView，尺寸=格子区），全部接线完成——
    /// 输入由全局「手」（HeldItemController autoload）统一接管，无需再挂每仓库控制器。
    /// 视图节点从全局对象池取（key=storageDef）——同一配置的仓库反复创建/清除会复用同一实例；
    /// 图案无效（ValidPattern 为空）时不生成：报错并返回 null。
    /// </summary>
    public static InventoryView? CreateStorage(Control parent, StorageTypeDef storageDef, Vector2 position)
    {
        var space = storageDef.ToSpace();
        if (space == null)
        {
            GD.PushError($"[InventoryView] 仓库配置无效：「{storageDef.DisplayName}」(Id={storageDef.Id}) 的 ValidPattern 为空，不生成仓库。");
            return null;
        }

        var pool = ObjectPool.Instance;
        var view = pool != null ? pool.Get(storageDef, static () => new InventoryView())
                                : new InventoryView();
        parent.AddChild(view);
        view.Position = position;
        view.Size = (Vector2)(space.Size * storageDef.CellSize);   // 仓库网格左上角 = 视图左上角（GridOffset=0）

        var inventory = new SpatialInventory(space);
        view.Setup(inventory, cellSize: storageDef.CellSize,
            gridOffset: Vector2I.Zero, storageLook: storageDef);
        return view;
    }

    /// <summary>
    /// 销毁并回收仓库（区别于隐藏）：数据销毁、视图归还对象池。若「手」正拿着从它拿走的物品，
    /// 按需求「销毁时手持物品保留」——物品留在手里，溯源清除（无家可归但不会回仓）。对象池不可用时直接释放视图。
    /// </summary>
    public static void RemoveStorage(InventoryView view)
    {
        if (view == null || !IsInstanceValid(view)) return;
        view.Inventory.Clear();
        HeldItemController.Instance?.ForgetSource(view);   // 手里若是从它拿的 → 留手、清溯源
        var pool = ObjectPool.Instance;
        if (pool != null) pool.Return(view);
        else view.QueueFree();
    }

    /// <summary>
    /// 把本仓库视图的可交互区扩大到「背景盒」覆盖范围（默认视图大小 = 格子区；背景贴图按
    /// BackgroundTextureSize×CellSize、以格子区中心±BackgroundTextureOffset 放置，往往比格子区大）。
    /// 让点击背景盒边缘（格子区以外的可见背景）也能落在视图上：空手按住 → 整仓拖拽
    /// （而不是掉进宿主的「点击仓库外 = 关闭」）。无背景贴图/背景没盖满格子区时不扩（保持格子区）。
    /// **幂等**：盒左上角相对「网格原点」的偏移是固定常量（rel = 网格中心 + 背景偏移 − 盒半尺寸，
    /// 与当前 GridOffset 无关），再把视图原点移到盒左上角 → GridOffset = −rel 恒等。这样同一视图反复
    /// CreateStorage/Hide/Show 都得到一致的几何，不会因开合次数把仓库向左上漂移。
    /// 与 _Draw.DrawGridGround 的背景盒计算同款（groundRect 中心 = 网格中心）。放大后 GridOffset 相应平移，
    /// 网格/物品/命中判定照旧（都是 GridOffset 相对量）。
    /// </summary>
    public void ExpandToBackground(StorageTypeDef storage)
    {
        if (storage.BackgroundTexture == null) return;
        var bg = storage.BackgroundTextureSize;
        if (bg.X <= 0f || bg.Y <= 0f) return;
        var cell = (float)CellSize;
        var gridPx = (Vector2)Inventory.Size * cell;
        var boxSize = bg * cell;
        if (boxSize.X < gridPx.X || boxSize.Y < gridPx.Y) return;   // 背景没盖满格子区 → 维持原样

        var rel = gridPx / 2f + storage.BackgroundTextureOffset * cell - boxSize / 2f;   // 盒左上角相对网格原点的偏移（常量）
        GridOffset = new Vector2I(Mathf.RoundToInt(-rel.X), Mathf.RoundToInt(-rel.Y));    // 新视图原点 = 盒左上角
        Size = new Vector2(boxSize.X, boxSize.Y);
    }

    public override void _Process(double delta)
    {
        // 10×10 规模下每帧重绘开销极小，换取实现简单、交互实时。
        QueueRedraw();
    }

    /// <summary>接收落在本视图上的鼠标事件，转交给全局「手」（HeldItemController）统一处理
    /// 或进入整仓拖拽。分流规则（前提=仓库开着）：
    ///  ▪ 左键按下 + 手持放置物 → 交给手（放置/交换/冲突保持，本视图内完成）；
    ///  ▪ 左键按下 + 空手 + 点到放置物 → 交给手（拾起）；
    ///  ▪ 左键按下 + 空手 + 点到仓库背景/空格子（无物品）→ 进入整仓拖拽（StartDrag），
    ///    按住期间每帧由移动事件驱动面板跟随光标（MoveDragTo），松开结束（EndDrag）；
    ///  ▪ 右键 → 旋转手持物品（交给手）。
    /// 拖拽的鼠标捕获由 GUI 保证：按下后移动/松开事件持续派给本视图，不会泄到宿主的「外部点击」判定。</summary>
    public override void _GuiInput(InputEvent @event)
    {
        var hand = HeldItemController.Instance;
        switch (@event)
        {
            case InputEventMouseButton mb when mb.Pressed && mb.ButtonIndex == MouseButton.Left:
                if (hand == null) return;
                // 空手 + 点到背景/空格子（无物品可拾）→ 整仓随鼠标移动；否则交回手（拾起或放置）。
                if (hand.HeldItem == null && !hand.IsRotateAnimating && !GetItemAtCell(mb.Position))
                {
                    StartDrag(mb.Position);   // mb.Position 已是视图本地坐标
                    return;
                }
                hand.OnLeftPressed(this, mb.Position);   // Position 已是视图本地坐标
                break;

            case InputEventMouseButton mb when !mb.Pressed && mb.ButtonIndex == MouseButton.Left && _dragActive:
                EndDrag();
                break;

            case InputEventMouseMotion when _dragActive:
                MoveDragTo(GetGlobalMousePosition());
                break;

            case InputEventMouseButton mb when mb.Pressed && mb.ButtonIndex == MouseButton.Right:
                hand?.OnRightPressed();
                break;
        }
    }

    /// <summary>按下点（视图本地）落格是否已有物品（用于区分「拾起」还是「拖拽」）。</summary>
    private bool GetItemAtCell(Vector2 localPos) => Inventory.GetItemAt(ToCell(localPos)) != null;

    /// <summary>开始整仓拖拽：记录按下点（视图本地 = 相对仓库左上角的抓取偏移）。
    /// 手持放置物时调用无效（拖拽只在空手时成立，与 _GuiInput 分流一致，这里再做一次防御）。</summary>
    public void StartDrag(Vector2 viewLocalPos)
    {
        if (HeldItemController.Instance?.HeldItem != null) return;
        _dragActive = true;
        _dragGrabLocal = viewLocalPos;
        _dragStartPos = Position;
    }

    /// <summary>拖拽中每帧调用：把面板左上角移到「光标当前位置 − 抓取偏移」，被抓的那一点（按下的背景/空格）
    /// 始终钉在光标下 → 整座仓库（背景/格子/放置物/打开中心）一起跟随平移。
    /// cursorPosition 与 <see cref="CanvasItem.GetGlobalMousePosition"/> 同空间（本视图所在图层的全局坐标）。</summary>
    public void MoveDragTo(Vector2 cursorPosition)
    {
        if (!_dragActive) return;
        if (GetParent() is Control parent)
            Position = parent.GetGlobalTransformWithCanvas().AffineInverse() * cursorPosition - _dragGrabLocal;
        else
            Position = cursorPosition - _dragGrabLocal;
    }

    /// <summary>松开左键结束拖拽：位置确有变化才通知（宿主据此记位置/刷新状态）。</summary>
    public void EndDrag()
    {
        if (!_dragActive) return;
        _dragActive = false;
        if (Position != _dragStartPos)
            Moved?.Invoke();
    }

    /// <summary>像素坐标（视图本地）→ 格子坐标（不裁剪，允许负数与越界值，由容器判定）。</summary>
    public Vector2I ToCell(Vector2 localPos)
    {
        var p = localPos - (Vector2)GridOffset;
        return new Vector2I(Mathf.FloorToInt(p.X / CellSize), Mathf.FloorToInt(p.Y / CellSize));
    }

    /// <summary>
    /// 子格精度悬停格：鼠标位置 → 「悬停格」，按锚点类型选择检测粒度，让幽灵预览/真实落位与视觉跟随对齐。
    /// snapFraction 为锚点在抓手格内的 0..1 比例（中心=0.5，边中点/角=0 或 1）：
    ///  ▪ 中心：整格检测（鼠标落在哪个格）；
    ///  ▪ 竖边中点：左右 1/2 单独判 → 锚点贴鼠标就近的竖格线；
    ///  ▪ 横边中点：上下 1/2 单独判 → 锚点贴鼠标就近的横格线；
    ///  ▪ 角：两轴都按半格细分（每格分左上/右上/左下/右下四部分）→ 锚点贴鼠标就近的格点。
    /// 锚点始终贴在鼠标就近的格线/格点上，占格图案随追踪以半格粒度精确换格，不再差出半格的视觉偏移。
    /// </summary>
    public Vector2I ToSubCell(Vector2 localPos, Vector2 snapFraction)
    {
        var q = (localPos - (Vector2)GridOffset) / CellSize;
        return new Vector2I(
            SubCellAxis(q.X, snapFraction.X),
            SubCellAxis(q.Y, snapFraction.Y));
    }

    /// <summary>单轴子格检测：中心锚点取所在整格；边缘锚点（0 或 1）取鼠标就近的格线（0=左/上缘，1=右/下缘）。</summary>
    private static int SubCellAxis(float q, float fraction)
    {
        if (fraction == 0.5f) return Mathf.FloorToInt(q);
        var line = Mathf.FloorToInt(q + 0.5f);  // 半格细分：鼠标就近的格线索引
        return fraction == 1f ? line - 1 : line;
    }

    /// <summary>格子坐标 → 网格中该格左上角的像素坐标（视图本地）。</summary>
    public Vector2 CellToPixel(Vector2I cell) =>
        (Vector2)GridOffset + (Vector2)(cell * CellSize);

    public override void _Draw()
    {
        if (Inventory == null) return;
        DrawGridGround();
        foreach (var item in Inventory.PlacedItems)
        {
            var originPx = CellToPixel(item.Origin);
            if (ItemRenderer.TryGetDefTexture(item, out var texture, out var def))
            {
                // 整张贴图：旋转内容而非重新拉伸（DrawOffset 在贴图自身坐标，随内容一起旋转）
                var cur = ItemRenderer.SpriteCurrentSize(item.Shape, def, CellSize, item.RotationTurns);
                var baseSize = ItemRenderer.SpriteBaseSize(cur, item.RotationTurns);
                ItemRenderer.DrawItemSpriteRotated(this, texture, originPx + cur / 2f, baseSize,
                    -baseSize / 2f + def.DrawOffset * CellSize,
                    item.RotationTurns * Mathf.Pi / 2f, Colors.White);
            }
            else
                ItemRenderer.DrawItemCells(this, item.Shape, originPx, ItemRenderer.FallbackCellColor, 1f, CellSize);
            // 占位色块
        }
        // 手持态不在这里：全局手（HeldItemController）的高图层 HandRenderLayer 负责，永远在各仓库之上。
    }

    private void DrawGridGround()
    {
        var origin = (Vector2)GridOffset;
        var cell = (float)CellSize;
        var gridSize = (Vector2)(Inventory.Size * CellSize);

        // 背景区矩形：不画任何底色/外框——有效格只画单格贴图（无贴图则纯色），凹陷格与矩形以外
        // 直接透出关卡背景；背景贴图按配置尺寸以该矩形中心（±偏移）放置。
        const float padding = 5f;
        var groundRect = new Rect2(origin - Vector2.One * padding,
            gridSize + Vector2.One * padding * 2f);

        // 背景贴图：不拉伸铺满，按 BackgroundTextureSize×CellSize 的尺寸、以背景区中心（±偏移）放置
        if (_storageLook?.BackgroundTexture is { } bg)
        {
            var bgSize = _storageLook.BackgroundTextureSize * cell;
            var center = groundRect.GetCenter() + _storageLook.BackgroundTextureOffset * cell;
            DrawTextureRect(bg, new Rect2(center - bgSize / 2f, bgSize), false);
        }

        for (var y = 0; y < Inventory.Size.Y; y++)
        for (var x = 0; x < Inventory.Size.X; x++)
        {
            var pos = origin + new Vector2(x * cell, y * cell);
            var rect = new Rect2(pos, new Vector2(cell, cell));
            if (Inventory.IsCellValid(new Vector2I(x, y)))
            {
                // 有效格：单格贴图拉伸铺满该格（无贴图则纯色）；格上已放置物品时改用
                // 「放置后底纹」CellTextureWithItem（缺省回退空置底纹 CellTexture）。
                var tile = Inventory.GetItemAt(new Vector2I(x, y)) != null
                    ? _storageLook?.CellTextureWithItem ?? _storageLook?.CellTexture
                    : _storageLook?.CellTexture;
                if (tile is { } t)
                    DrawTextureRect(t, rect, false);
                else
                    DrawRect(rect, CellFill, true);
            }
            // 凹陷/不可用格：什么都不画 → 直接透出关卡背景
        }
    }
}
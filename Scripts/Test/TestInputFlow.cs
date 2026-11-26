using System.Linq;
using Godot;
using SaleFish.Common;
using SaleFish.Inventory;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 输入链路集成测试：直接向视图派发 mouse 事件（等价于引擎把真实点击交给 _gui_input），
/// 验证：左键拿起 → 右键旋转 → 左键放下，即“视图→全局手(controller)→容器”各层衔接是否正确。
/// 注：headless 下引擎不对外来合成事件做 GUI 命中，故不依赖 Viewport.PushInput。
/// </summary>
public sealed partial class TestInputFlow : Control
{
    private SpatialInventory _inventory = null!;
    private InventoryView _view = null!;
    private InventoryItem _a = null!;
    private int _step;
    private int _passed;
    private int _failed;

    /// <summary>全局「手」（autoload 单例）：本测试所有交互都经由它，不再有每仓库控制器。</summary>
    private static HeldItemController Hand => HeldItemController.Instance;

    public override void _Ready()
    {
        _inventory = new SpatialInventory(StorageSpace.Rectangle(new Vector2I(10, 10)));
        _view = new InventoryView();
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        _view.Setup(_inventory, 52, new Vector2I(100, 92));
        // 主流程测试按帧推进，旋转动画会阻塞「旋转后立刻放下」；动画的开关行为单独在 TestRotateAnimation 验证。
        Hand.EnableRotateAnimation = false;

        // 预放一个 3×1 横条在 (0,0)
        _a = new InventoryItem(PlacementShape.Line(3));
        _inventory.TryPlace(_a, new Vector2I(0, 0));
    }

    public override void _Process(double delta)
    {
        switch (_step)
        {
            case 0:
                // 点击横条中间的格子 (1,0)，应被拿起
                ClickCell(new Vector2I(1, 0), MouseButton.Left);
                break;
            case 1:
                Check("左键点击拿起物品", Hand.HeldItem == _a);
                RightClick();
                break;
            case 2:
                // 旋转后应是竖条
                Check("右键旋转 90° 生效", _a.Shape.ToPattern().SequenceEqual(new[] { "X", "X", "X" }));
                // 旋转后抓手被重置为居中 (0,1)。落下锚点 = 悬停格 - 抓手。
                // 想让竖条落在 (1,0)(1,1)(1,2)（锚点 (1,0)），需点击 (1,0)+(0,1)=(1,1)。
                ClickCell(new Vector2I(1, 1), MouseButton.Left);
                break;
            case 3:
                Check("左键放下到新位置",
                    Hand.HeldItem == null
                    && _inventory.GetItemAt(new Vector2I(1, 0)) == _a
                    && _inventory.GetItemAt(new Vector2I(1, 1)) == _a
                    && _inventory.GetItemAt(new Vector2I(1, 2)) == _a);
                TestInstantFollow();
                TestPivotRotation();
                TestGrabSnap();
                TestRotateAnimation();
                TestSubCellHover();
                TestInitialAnchor();
                TestGhostFilter();
                TestWarehousePool();
                TestCrossWarehouse();
                TestWarehouseLifecycle();
                break;
            case 4:
                GD.Print($"[InputTest] PASS={_passed} FAIL={_failed}");
                GD.Print(_failed == 0 ? "[InputTest] ALL PASSED" : "[InputTest] HAS FAILURES");
                GetTree().Quit(_failed == 0 ? 0 : 1);
                break;
        }
        _step++;
    }

    /// <summary>视觉跟进测试：手持物品直接对齐「鼠标 - 抓手像素偏移」，无平滑，拿起瞬间零跳变。</summary>
    private void TestInstantFollow()
    {
        // 1) 真实拿起路径：落到 (3,3)，点击其内部一点拿起。
        var f = new InventoryItem(PlacementShape.Rectangle(2, 2));
        _inventory.TryPlace(f, new Vector2I(3, 3));
        var originPx = _view.CellToPixel(new Vector2I(3, 3));
        var clickPx = originPx + new Vector2(10, 10); // 物品左上的内部点
        Hand.OnLeftPressed(_view, clickPx);
        Check("拿起瞬间视觉对齐原位", (Hand.HeldVisPos - originPx).Length() < 0.01f);

        // 2) 鼠标停在原位不动，更新一帧后抓取点（吸附到的角点）精确钉在原点击点下、无漂移。
        Hand.AdvanceFollow(clickPx);
        Check("鼠标静止不漂移(抓取点钉在鼠标下)", (HeldPixel() - clickPx).Length() < 0.01f);

        // 3) 鼠标远移：单帧即精确到位（无平滑收敛过程）。
        var mouseB = new Vector2(600, 400);
        var expectB = mouseB - Hand.GrabVisualOffset;
        Hand.AdvanceFollow(mouseB);
        Check("单帧即时对齐", (Hand.HeldVisPos - expectB).Length() < 0.5f);

        Hand.DiscardHeld(); // 清理，避免干扰后续测试
    }

    /// <summary>旋转中心 = 点击的格子：点击 3×1 的最右格抓起，旋转后该格仍钉在鼠标下。</summary>
    private void TestPivotRotation()
    {
        var item = new InventoryItem(PlacementShape.Line(3));
        _inventory.TryPlace(item, new Vector2I(6, 0)); // 占 (6,0)(7,0)(8,0)

        // 点击最右格 (8,0) 的中心拿起 → 抓手应为 (2,0)。
        var clickPx = (Vector2)_view.GridOffset
                      + (Vector2)(new Vector2I(8, 0) * _view.CellSize)
                      + new Vector2(_view.CellSize / 2f, _view.CellSize / 2f);
        Hand.OnLeftPressed(_view, clickPx);
        Check("点击最右格成为抓手", Hand.GrabCell == new Vector2I(2, 0));
        Check("抓取点正好在鼠标下", (HeldPixel() - clickPx).Length() < 0.01f);

        // 旋转：3×1 → 1×3。旧格 (2,0)（旧高 1）→ 新格 (0,2)，物理上仍是刚抓住的那格。
        Hand.OnRightPressed();
        Check("旋转后抓手随形变换", Hand.GrabCell == new Vector2I(0, 2));
        Check("旋转后仍是 1×3 竖条", item.Shape.ToPattern().SequenceEqual(new[] { "X", "X", "X" }));

        // 鼠标不动，更新一帧后抓取点仍钉在鼠标下（旋转中心=点击格，无跳变）。
        Hand.AdvanceFollow(clickPx);
        Check("旋转后抓取点仍贴手", (HeldPixel() - clickPx).Length() < 0.5f);

        Hand.DiscardHeld();
    }

    /// <summary>抓取吸附：点击落点被吸附到所在格可用锚点中最近的一个（规则见 PlacementShape.GrabAnchorFractions）。</summary>
    private void TestGrabSnap()
    {
        var g = new InventoryItem(PlacementShape.Rectangle(2, 2));
        _inventory.TryPlace(g, new Vector2I(5, 5));
        var tl = _view.CellToPixel(new Vector2I(5, 5)); // 2×2 块的左上格 (5,5)
        var c = (float)_view.CellSize;
        var center = new Vector2(c / 2f, c / 2f);

        // 1) 点击左上格的外角附近 → 外角被过滤（仅 1 格共用），吸附到最近可用点：中心
        Hand.OnLeftPressed(_view, tl + new Vector2(3f, 4f));
        Check("外角被排除→吸附中心", (Hand.GrabVisualOffset - center).Length() < 0.01f);

        // 2) 点击内部交叉角附近（4 格共用）→ 该角可用
        Hand.DiscardHeld();
        _inventory.TryPlace(g, new Vector2I(5, 5));
        Hand.OnLeftPressed(_view, tl + new Vector2(c - 2f, c - 2f));
        Check("4 格共用角可用", (Hand.GrabVisualOffset - new Vector2(c, c)).Length() < 0.01f);

        // 3) 点击上排右格的左边界中点附近（与左格共边）→ 边中点可用
        Hand.DiscardHeld();
        _inventory.TryPlace(g, new Vector2I(5, 5));
        Hand.OnLeftPressed(_view, tl + new Vector2(c, c / 2f));
        Check("共用边中点为锚点", (Hand.GrabVisualOffset - new Vector2(c, c / 2f)).Length() < 0.01f);

        Hand.DiscardHeld();
    }

    /// <summary>旋转动画开关：启用时旋转约 duration 秒（期间禁止再旋转与放置）；禁用时保持即时旋转。</summary>
    private void TestRotateAnimation()
    {
        var r = new InventoryItem(PlacementShape.Line(3));
        _inventory.TryPlace(r, new Vector2I(0, 5));
        // 抓起最左格的中心作为锚点（锚点离盒中心 52px → 旋转动画中盒中心绕鼠标画 52px 半径的弧）
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(0, 5)) + new Vector2(26, 26));
        Check("已拿起用于动画测试", Hand.HeldItem == r);

        Hand.EnableRotateAnimation = true;
        Hand.RotateAnimationDuration = 0.2;

        Hand.OnRightPressed();
        Check("旋转动画已启动", Hand.IsRotateAnimating);
        Check("形状立即切换为新朝向", r.Shape.ToPattern().SequenceEqual(new[] { "X", "X", "X" }));

        // 方案 B：旋转轴 = 锚点 = 鼠标——动画中移动鼠标，锚点紧跟光标、始终钉在光标下。
        Hand.AdvanceFollow(new Vector2(500, 400));
        Check("动画中锚点钉在鼠标下", (HeldPixel() - new Vector2(500, 400)).Length() < 0.01f);

        // 几何：盒中心 = 锚点 − R_θ(g₀)；45° 时轨道分量 = |g₀|/√2；90° 终点与正常跟随目标位重合（零跳变）。
        var g0 = Hand.RotateFromGrabOffset - new Vector2(156f, 52f) / 2f; // 3×1 的「锚点-盒中心」向量
        var expect = g0.Length() / Mathf.Sqrt(2f);
        var rotated45 = ItemRenderer.RotateVector(g0, Mathf.Pi / 4f);
        Check("45° 轨道分量为 |g₀|/√2",
            Mathf.Abs(Mathf.Abs(rotated45.X) - expect) < 0.5f && Mathf.Abs(Mathf.Abs(rotated45.Y) - expect) < 0.5f);
        Check("90° 终点与跟随目标重合",
            (HeldPixel() - ItemRenderer.RotateVector(g0, Mathf.Pi / 2f)
             - (Hand.HeldVisPos + new Vector2(52f, 156f) / 2f)).Length() < 0.5f);

        var before = r.Shape.ToPattern().ToArray();
        Hand.OnRightPressed(); // 动画中再次旋转 → 无效果
        Check("动画中禁止再次旋转", r.Shape.ToPattern().SequenceEqual(before));

        Hand.OnLeftPressed(_view, new Vector2(300, 300)); // 动画中放置 → 无效果
        Check("动画中禁止放下(仍手持)", Hand.HeldItem == r);
        Check("动画中禁止放下(未落格)", !_inventory.PlacedItems.Contains(r));

        Hand.TickRotateAnimation(0.1);
        Check("未到时长仍在动画中", Hand.IsRotateAnimating);

        Hand.TickRotateAnimation(0.2); // 累计 0.3 ≥ 0.2 → 动画完成
        Check("动画结束后可操作", !Hand.IsRotateAnimating);

        Hand.OnRightPressed(); // 完成后可再次旋转：1×3 竖条 → 3×1 横条
        Check("动画结束可再次旋转", r.Shape.Width == 3 && r.Shape.ToPattern().SequenceEqual(new[] { "XXX" }));

        Hand.DiscardHeld();
        Hand.EnableRotateAnimation = false; // 恢复，不影响后续用例
    }

    /// <summary>子格精度悬停：锚点为竖边中点/角时，放置按半格细分贴到鼠标就近的格线/格点，落位随视觉走。</summary>
    private void TestSubCellHover()
    {
        // 1) 竖边中点锚点：2×1 放 (0,8)，点击共享边中点拿起 → 抓手格 (1,0)，锚点 (0, 0.5)
        var f = new InventoryItem(PlacementShape.Rectangle(2, 1));
        _inventory.TryPlace(f, new Vector2I(0, 8));
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(0, 8)) + new Vector2(52, 26));
        Check("2×1 拿起为竖边中点锚点", Hand.GrabCell == new Vector2I(1, 0)
              && Hand.GrabSnapFraction == new Vector2(0f, 0.5f));

        // 鼠标在格 (3,3) 左 1/2 → 锚该格左边缘，占 (2,3)(3,3)
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(3, 3)) + new Vector2(8, 26));
        Check("竖边中点·左1/2 锚左边缘", _inventory.GetItemAt(new Vector2I(2, 3)) == f
              && _inventory.GetItemAt(new Vector2I(3, 3)) == f);

        // 再拿起；鼠标在格 (3,3) 右 1/2 → 锚该格右边缘，占 (3,3)(4,3)
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(2, 3)) + new Vector2(52, 26));
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(3, 3)) + new Vector2(44, 26));
        Check("竖边中点·右1/2 锚右边缘", _inventory.GetItemAt(new Vector2I(3, 3)) == f
              && _inventory.GetItemAt(new Vector2I(4, 3)) == f);

        Hand.DiscardHeld();

        // 2) 角锚点：2×2 放 (5,5)，点击内角附近拿起 → 抓手格 (1,1)，锚点 (0, 0)（4 格共用角）
        var g = new InventoryItem(PlacementShape.Rectangle(2, 2));
        _inventory.TryPlace(g, new Vector2I(5, 5));
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(5, 5)) + new Vector2(54, 54));
        Check("2×2 拿起为角锚点", Hand.GrabCell == new Vector2I(1, 1)
              && Hand.GrabSnapFraction == Vector2.Zero);

        // 鼠标在格 (8,8) 左上 1/4 → 锚最就近的格点，占 (7,7)~(8,8)
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(8, 8)) + new Vector2(8, 8));
        Check("角锚点·左上1/4 锚共用角", _inventory.GetItemAt(new Vector2I(7, 7)) == g
              && _inventory.GetItemAt(new Vector2I(8, 8)) == g);

        // 再拿起；鼠标在格 (8,8) 右下 1/4 → 锚相邻格点，占 (8,8)~(9,9)
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(7, 7)) + new Vector2(54, 54));
        Hand.OnLeftPressed(_view, _view.CellToPixel(new Vector2I(8, 8)) + new Vector2(48, 48));
        Check("角锚点·右下1/4 锚邻格点", _inventory.GetItemAt(new Vector2I(9, 9)) == g
              && _inventory.GetItemAt(new Vector2I(8, 8)) == g);

        Hand.DiscardHeld();
    }

    /// <summary>初始锚点（PlacementItemTypeDef.InitialGrabCell/InitialSnap）：生成时按定义取锚，未定义/非法回退。</summary>
    private void TestInitialAnchor()
    {
        // 1) 2×2 定义内角为初始锚点：抓手格 (1,1)、格内 (0,0)（4 格共用角，合法）
        var def = new PlacementItemTypeDef
        {
            ShapePattern = new[] { "XX", "XX" },
            InitialGrabCell = new Vector2I(1, 1),
            InitialSnap = new Vector2(0f, 0f),
        };
        Hand.TakeExternal(def.ToItem());
        Check("初始锚点·用定义抓手格", Hand.GrabCell == new Vector2I(1, 1));
        Check("初始锚点·角锚点生效", Hand.GrabSnapFraction == Vector2.Zero);
        Hand.DiscardHeld();

        // 2) 非法角（1×1 的角只有 1 格共用）→ 回退到该格最近合法锚点（中心）
        var bad = new PlacementItemTypeDef
        {
            ShapePattern = new[] { "X" },
            InitialGrabCell = new Vector2I(0, 0),
            InitialSnap = new Vector2(0f, 0f),
        };
        Hand.TakeExternal(bad.ToItem());
        Check("初始锚点·非法角回退中心", Hand.GrabCell == new Vector2I(0, 0)
              && Hand.GrabSnapFraction == new Vector2(0.5f, 0.5f));
        Hand.DiscardHeld();

        // 3) 未定义初始锚点 → 居中（原有行为）
        var plain = new PlacementItemTypeDef { ShapePattern = new[] { "XXX" } };
        Hand.TakeExternal(plain.ToItem());
        Check("初始锚点·未定义回退居中", Hand.GrabCell == new Vector2I(1, 0)
              && Hand.GrabSnapFraction == new Vector2(0.5f, 0.5f));
        Hand.DiscardHeld();
    }

    /// <summary>幽灵块过滤：目标格不在可放置仓库区内（越界/凹陷）的占格不生成幽灵块（ItemRenderer.GhostOccupiedCells）。</summary>
    private void TestGhostFilter()
    {
        // 5×5 中央挖一个洞的仓库
        var hole = new SpatialInventory(StorageSpace.FromPattern(
            "#####",
            "#.###",
            "#####",
            "#####",
            "#####"));
        var line = PlacementShape.Line(3);

        Check("幽灵过滤·全在区内画满", CountGhost(hole, line, new Vector2I(0, 0)) == 3);
        Check("幽灵过滤·越界部分不画", CountGhost(hole, line, new Vector2I(3, 0)) == 2);   // (5,0) 越界
        Check("幽灵过滤·凹陷处不画", CountGhost(hole, line, new Vector2I(0, 1)) == 2);      // (1,1) 是洞
        Check("幽灵过滤·矩形跨右边界", CountGhost(hole, PlacementShape.Rectangle(2, 2), new Vector2I(4, 0)) == 2);
    }

    /// <summary>形状放在 anchor 处时按规则应绘制几个幽灵格。</summary>
    private static int CountGhost(SpatialInventory inventory, PlacementShape shape, Vector2I anchor) =>
        ItemRenderer.GhostOccupiedCells(inventory, anchor, shape).Count();

    /// <summary>仓库工厂 + 对象池：CreateStorage 创建整套仓库；RemoveStorage 摘树回池；同配置再建复用同一实例。</summary>
    private void TestWarehousePool()
    {
        var pool = ObjectPool.Instance;
        Check("对象池 autoload 可用", pool != null);
        if (pool == null) return;

        var def = new StorageTypeDef
        {
            Id = 920,
            DisplayName = "池测试仓库",
            CellSize = 52,
            ValidPattern = new[] { "###", "###" },   // 3×2
        };

        var first = InventoryView.CreateStorage(this, def, new Vector2(40, 480));
        Check("创建仓库返回视图", first != null);
        Check("仓库已挂入场景", first!.GetParent() == this);
        Check("视图尺寸=格子区(3×2)", first.Size == new Vector2(156, 104) && first.CellSize == 52);

        InventoryView.RemoveStorage(first);
        Check("回收后摘树离场(未释放)", first.GetParent() == null && IsInstanceValid(first));

        var second = InventoryView.CreateStorage(this, def, new Vector2(40, 480));
        Check("同配置再建复用池实例", ReferenceEquals(first, second));
        Check("复用后真正挂回场景", second!.GetParent() == this);
        InventoryView.RemoveStorage(second);
    }

    /// <summary>跨仓库互通：全局手持的物品点击乙即落入乙；落在被占格上则交换顶起原物品；冲突保持手持。</summary>
    private void TestCrossWarehouse()
    {
        var defA = new StorageTypeDef { Id = 931, DisplayName = "甲", CellSize = 52, ValidPattern = new[] { "####", "####" } };
        var defB = new StorageTypeDef { Id = 932, DisplayName = "乙", CellSize = 52, ValidPattern = new[] { "####", "####" } };
        var wa = InventoryView.CreateStorage(this, defA, new Vector2(40, 620));
        var wb = InventoryView.CreateStorage(this, defB, new Vector2(320, 620));
        Check("跨仓库·甲乙创建", wa != null && wb != null);
        if (wa == null || wb == null) return;

        var item = new InventoryItem(PlacementShape.Rectangle(2, 2));   // 2×2
        var other = new InventoryItem(PlacementShape.Rectangle(1, 1));  // 1×1

        // 乙先放一个 1×1 占 (0,0)
        Hand.TakeExternal(other, wb);
        ClickView(wb, new Vector2I(0, 0), MouseButton.Left);
        Check("跨仓库·乙(0,0)有占位", wb.Inventory.GetItemAt(Vector2I.Zero) == other);

        // 甲拿起 2×2，直接点乙的空位（网格角点 (3,1) → 物品原点 (2,0)）→ 落入乙，手清空
        Hand.TakeExternal(item, wa);
        ClickPixel(wb, wb.CellToPixel(new Vector2I(3, 1)), MouseButton.Left);
        Check("跨仓库·物品落入乙", wb.Inventory.GetItemAt(new Vector2I(2, 0)) == item
              && Hand.HeldItem == null && wa.Inventory.PlacedItems.Count() == 0);

        // 在乙内拿起 item
        ClickPixel(wb, wb.CellToPixel(item.Origin) + new Vector2(26, 26), MouseButton.Left);
        Check("跨仓库·乙内拿起", Hand.HeldItem == item);

        // 从乙跨回甲：点甲的空位 (0,0) 中心 → 落回甲
        ClickView(wa, new Vector2I(0, 0), MouseButton.Left);
        Check("跨仓库·落回甲", wa.Inventory.GetItemAt(Vector2I.Zero) == item && Hand.HeldItem == null);

        // 甲拿起 item，再点乙的 (0,0)（other 占着）→ 交换：item 落位，other 顶起
        ClickPixel(wa, wa.CellToPixel(item.Origin) + new Vector2(26, 26), MouseButton.Left);
        Check("跨仓库·甲再拿起", Hand.HeldItem == item);
        ClickView(wb, new Vector2I(0, 0), MouseButton.Left);
        Check("跨仓库·交换顶起", wb.Inventory.GetItemAt(Vector2I.Zero) == item
              && Hand.HeldItem == other && wa.Inventory.PlacedItems.Count() == 0);

        // 跨仓库右键旋转：手持物品悬在乙上，右键必须直接旋转手持物（旋转与落点无关，作用于物品本身）。
        Hand.EnableRotateAnimation = false;   // 单帧内连转两次，绕开动画防重入
        var bar = new InventoryItem(PlacementShape.Line(3));   // 1×3 横条
        Hand.TakeExternal(bar, wa);
        Check("跨仓库·甲手持横条", Hand.HeldItem == bar && bar.Shape.Size == new Vector2I(3, 1));
        ClickPixel(wb, new Vector2(30, 40), MouseButton.Right);
        Check("跨仓库·悬乙上右键→竖条", Hand.HeldItem == bar
              && bar.RotationTurns == 1 && bar.Shape.Size == new Vector2I(1, 3));
        ClickPixel(wb, new Vector2(30, 40), MouseButton.Right);
        Check("跨仓库·悬乙上再右键可继续转", Hand.HeldItem == bar
              && bar.RotationTurns == 2 && bar.Shape.Size == new Vector2I(3, 1));

        Hand.DiscardHeld();
        InventoryView.RemoveStorage(wa);
        InventoryView.RemoveStorage(wb);
    }

    /// <summary>仓库生命周期 × 全局手：Hide 把手持物品放回来源仓库（原位 → 第一个合法空位 → 保持手持），
    /// 旋转一并复原到拿起时；Destroy(RemoveStorage) 则物品留手、溯源清除，仍可放入其他仓库。</summary>
    private void TestWarehouseLifecycle()
    {
        var defA = new StorageTypeDef { Id = 941, DisplayName = "甲", CellSize = 52, ValidPattern = new[] { "########", "########" } };
        var defB = new StorageTypeDef { Id = 942, DisplayName = "乙", CellSize = 52, ValidPattern = new[] { "########", "########" } };
        var wa = InventoryView.CreateStorage(this, defA, new Vector2(40, 820));
        var wb = InventoryView.CreateStorage(this, defB, new Vector2(400, 820));
        Check("生命周期·甲乙创建", wa != null && wb != null);
        if (wa == null || wb == null) return;

        // 1) 隐藏还原原位（连同旋转复原到拿起时状态）
        var bar = new InventoryItem(PlacementShape.Line(3));
        wa.Inventory.TryPlace(bar, new Vector2I(1, 0));            // 横条 (1,0)~(3,0)
        ClickPixel(wa, wa.CellToPixel(new Vector2I(2, 0)) + new Vector2(26, 26), MouseButton.Left); // 拾起
        Check("生命周期·甲拾起横条", Hand.HeldItem == bar);
        Hand.OnRightPressed();                                     // 拿起后转一次（拿起时 turns=0）
        Check("生命周期·拾起后转过", bar.RotationTurns == 1 && bar.Shape.Size == new Vector2I(1, 3));
        wa.Hide();                                                 // 关闭背包 → 放回原位并转回拿起时朝向
        Check("生命周期·隐藏后物品回原位", wa.Inventory.GetItemAt(new Vector2I(1, 0)) == bar
              && wa.Inventory.GetItemAt(new Vector2I(3, 0)) == bar);
        Check("生命周期·隐藏后空手", Hand.HeldItem == null);
        Check("生命周期·旋转一同复原", bar.RotationTurns == 0 && bar.Shape.Size == new Vector2I(3, 1));
        wa.Show();                                                 // 恢复显示、重新登记

        // 2) 交换顶起后隐藏：顶起物原位 (0,0) 已被落位的 2×2 占住 → 扫第一个合法空位 (2,0)
        var big = new InventoryItem(PlacementShape.Rectangle(2, 2));
        wa.Inventory.TryPlace(big, new Vector2I(4, 0));            // 甲里放 2×2
        ClickPixel(wa, wa.CellToPixel(new Vector2I(4, 0)) + new Vector2(26, 26), MouseButton.Left); // 拾起
        var other = new InventoryItem(PlacementShape.Rectangle(1, 1));
        wb.Inventory.TryPlace(other, Vector2I.Zero);               // 乙 (0,0) 放 1×1
        ClickView(wb, new Vector2I(0, 0), MouseButton.Left);       // 手持 big 点乙(0,0) → 交换：big 落位、other 顶起
        Check("生命周期·交换顶起", wb.Inventory.GetItemAt(Vector2I.Zero) == big && Hand.HeldItem == other);
        wb.Hide();                                                 // 关闭乙：other 原位被 big 占 → 扫第一个合法空位
        Check("生命周期·顶起物隐藏回退空位", wb.Inventory.GetItemAt(new Vector2I(2, 0)) == other && Hand.HeldItem == null);
        Check("生命周期·落位的 big 保持原位", wb.Inventory.GetItemAt(Vector2I.Zero) == big);
        wb.Show();

        // 3) 销毁仓库：物品留手、溯源清除，仍可放入其他仓库
        var carry = new InventoryItem(PlacementShape.Rectangle(1, 1));
        wa.Inventory.TryPlace(carry, new Vector2I(6, 0));
        ClickPixel(wa, wa.CellToPixel(new Vector2I(6, 0)) + new Vector2(26, 26), MouseButton.Left); // 拾起
        Check("生命周期·甲拾起 carry", Hand.HeldItem == carry);
        InventoryView.RemoveStorage(wa);                           // 销毁甲：物品应留在手上
        Check("生命周期·销毁后物品留手", Hand.HeldItem == carry);
        ClickView(wb, new Vector2I(5, 0), MouseButton.Left);       // 仍可放入乙
        Check("生命周期·销毁后仍可落位他仓", wb.Inventory.GetItemAt(new Vector2I(5, 0)) == carry
              && Hand.HeldItem == null);

        // 4) 无来源物品（生成）隐藏来源仓库不受影响：不会强行还回去
        var spawn = new InventoryItem(PlacementShape.Rectangle(1, 1));
        Hand.TakeExternal(spawn, wb);
        wb.Hide();
        Check("生命周期·无来源物品隐藏不受影响", Hand.HeldItem == spawn);
        Hand.DiscardHeld();
        wb.Show();

        InventoryView.RemoveStorage(wb);
    }

    /// <summary>手持物品当前被抓住的像素点（视觉锚点 + 抓手像素偏移；全局坐标）。</summary>
    private static Vector2 HeldPixel() => Hand.HeldVisPos + Hand.GrabVisualOffset;

    private void ClickCell(Vector2I cell, MouseButton button)
    {
        var half = _view.CellSize / 2f;
        var pixel = (Vector2)_view.GridOffset + (Vector2)(cell * _view.CellSize) + new Vector2(half, half);
        var evt = new InputEventMouseButton
        {
            ButtonIndex = button,
            Pressed = true,
            Position = pixel,
            GlobalPosition = pixel,
        };
        _view._GuiInput(evt); // 等价于真实引擎把落在视图上的点击交给 _gui_input
    }

    /// <summary>向指定仓库视图派发一次点击（网格坐标 → 格中心像素；CreateStorage 的视图 GridOffset=0）。</summary>
    private static void ClickView(InventoryView view, Vector2I cell, MouseButton button)
    {
        var half = view.CellSize / 2f;
        ClickPixel(view, (Vector2)(cell * view.CellSize) + new Vector2(half, half), button);
    }

    /// <summary>向指定仓库视图派发一次点击（指定像素，视图本地坐标）。</summary>
    private static void ClickPixel(InventoryView view, Vector2 pixel, MouseButton button)
    {
        var evt = new InputEventMouseButton
        {
            ButtonIndex = button,
            Pressed = true,
            Position = pixel,
            GlobalPosition = pixel,
        };
        view._GuiInput(evt);
    }

    private void RightClick()
    {
        var pixel = (Vector2)_view.GridOffset + new Vector2(10, 10);
        var evt = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = pixel,
            GlobalPosition = pixel,
        };
        _view._GuiInput(evt);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }
}
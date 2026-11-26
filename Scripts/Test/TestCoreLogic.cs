using System.Linq;
using Godot;
using SaleFish.Inventory;

namespace SaleFish.Test;

/// <summary>
/// 核心逻辑自动化测试（数学层不依赖渲染，可独立验证）。
/// 运行方式：godot --headless --path <project> res://Scenes/TestCoreLogic.tscn --quit-after 120
/// </summary>
public sealed partial class TestCoreLogic : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        Run();
        GD.Print($"[CoreTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[CoreTest] ALL PASSED" : "[CoreTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    private void Run()
    {
        // 1. 基本放置与占用
        var inv = new SpatialInventory(StorageSpace.Rectangle(new Vector2I(10, 10)));
        var a = new InventoryItem(PlacementShape.Line(3));
        Check("普通放置成功", inv.TryPlace(a, new Vector2I(0, 0)).Outcome == PlaceOutcome.Success);
        Check("放置后格子被占用", inv.GetItemAt(new Vector2I(2, 0)) == a);

        // 2. 交换：目标格只被 1 个物品占用 → 顶起换手
        var b = new InventoryItem(PlacementShape.Line(3));
        var swap = inv.TryPlace(b, new Vector2I(0, 0));
        Check("交换触发且顶起原物品", swap.Outcome == PlaceOutcome.Swap && swap.Bumped == a);
        Check("新物品占满原区域", inv.GetItemAt(new Vector2I(0, 0)) == b
              && inv.GetItemAt(new Vector2I(1, 0)) == b
              && inv.GetItemAt(new Vector2I(2, 0)) == b);

        // 3. 目标格被 ≥2 个不同物品占用 → 失败且状态不变
        var c1 = new InventoryItem(PlacementShape.Rectangle(1, 1));
        var c2 = new InventoryItem(PlacementShape.Rectangle(1, 1));
        var d = new InventoryItem(PlacementShape.Rectangle(2, 1));
        inv.TryPlace(c1, new Vector2I(5, 5));
        inv.TryPlace(c2, new Vector2I(6, 5));
        var multi = inv.TryPlace(d, new Vector2I(5, 5));
        Check("多物品冲突无法放置", multi.Outcome == PlaceOutcome.OverlapMulti);
        Check("冲突后原状态未改变",
            inv.GetItemAt(new Vector2I(5, 5)) == c1 && inv.GetItemAt(new Vector2I(6, 5)) == c2
            && inv.GetItemAt(new Vector2I(6, 5)) == c2 && inv.GetItemAt(new Vector2I(5, 6)) == null);

        // 4. 越界
        var e = new InventoryItem(PlacementShape.Line(3));
        Check("越界放置失败", inv.TryPlace(e, new Vector2I(9, 0)).Outcome == PlaceOutcome.OutOfBounds);

        // 5. 异形空间（凹陷）
        var holeSpace = StorageSpace.FromPattern("##..", "####", "####", "####");
        var holeInv = new SpatialInventory(holeSpace);
        var f = new InventoryItem(PlacementShape.Rectangle(1, 1));
        Check("落在凹陷格上失败", holeInv.TryPlace(f, new Vector2I(2, 0)).Outcome == PlaceOutcome.InvalidArea);
        Check("有效区照常放置", holeInv.TryPlace(f, new Vector2I(1, 0)).Outcome == PlaceOutcome.Success);

        // 6. 旋转（数学正确性）
        var vertical = PlacementShape.Line(3).RotatedClockwise();
        Check("3×1 旋转后变 1×3 竖条", vertical.Width == 1 && vertical.Height == 3);
        var l = PlacementShape.LShape().RotatedClockwise();
        Check("L 形旋转后仍 2×2 占 3 格", l.Width == 2 && l.Height == 2 && l.CellCount == 3);
        var lBase = PlacementShape.LShape();
        var l4 = lBase;
        for (var i = 0; i < 4; i++) l4 = l4.RotatedClockwise();
        Check("L 形转 4 次回到原形", l4.ToPattern().SequenceEqual(lBase.ToPattern()));

        // 7. 拿起释放格子
        var g = new InventoryItem(PlacementShape.Rectangle(2, 2));
        inv.TryPlace(g, new Vector2I(0, 3));
        Check("拿起前能查到物品", inv.GetItemAt(new Vector2I(1, 3)) == g);
        Check("拿起返回成功", inv.RemoveItem(g));
        Check("拿起后格子释放", inv.GetItemAt(new Vector2I(1, 3)) == null
              && inv.GetItemAt(new Vector2I(0, 3)) == null);

        // 8. 部分重叠的交换（目标区 = 空闲格 + 仅 1 个物品的格）
        var h1 = new InventoryItem(PlacementShape.Rectangle(1, 1));
        inv.TryPlace(h1, new Vector2I(3, 8));
        var h2 = new InventoryItem(PlacementShape.Rectangle(2, 1));
        var partial = inv.TryPlace(h2, new Vector2I(3, 8)); // 占 (3,8)=h1, (4,8)=空闲
        Check("部分重叠可交换", partial.Outcome == PlaceOutcome.Swap && partial.Bumped == h1);

        // 9. 抓取锚点规则（PlacementShape.GrabAnchorFractions，格内 0..1 比例坐标）
        var single = PlacementShape.Rectangle(1, 1);
        var singleAnchor = single.GrabAnchorFractions(new Vector2I(0, 0)).ToArray();
        Check("1×1 只有中心可用", singleAnchor.Length == 1 && singleAnchor[0] == new Vector2(0.5f, 0.5f));

        var line3 = PlacementShape.Line(3);
        var mid3 = line3.GrabAnchorFractions(new Vector2I(1, 0)).ToArray();
        Check("3×1 中间格=中心+左右边中点（无角）",
            mid3.Length == 3 && mid3.Contains(new Vector2(0f, 0.5f))
            && mid3.Contains(new Vector2(1f, 0.5f)) && mid3.Contains(new Vector2(0.5f, 0.5f)));
        var end3 = line3.GrabAnchorFractions(new Vector2I(0, 0)).ToArray();
        Check("3×1 端格=中心+内侧边中点",
            end3.Length == 2 && end3.Contains(new Vector2(1f, 0.5f)) && !end3.Contains(new Vector2(0f, 0.5f)));

        var sq2 = PlacementShape.Rectangle(2, 2);
        var sqTL = sq2.GrabAnchorFractions(new Vector2I(0, 0)).ToArray();
        Check("2×2 左上格含 4 格共用角(右下)", sqTL.Contains(new Vector2(1f, 1f)));
        Check("2×2 左上格排除外角(左上)", !sqTL.Contains(new Vector2(0f, 0f)));
        Check("2×2 左上格含两条内部边中点",
            sqTL.Contains(new Vector2(1f, 0.5f)) && sqTL.Contains(new Vector2(0.5f, 1f)));

        var t = PlacementShape.TShape();
        var tCenter = t.GrabAnchorFractions(new Vector2I(1, 1)).ToArray();
        Check("T 形中心格含 3 格共用角(上两角)",
            tCenter.Contains(new Vector2(0f, 0f)) && tCenter.Contains(new Vector2(1f, 0f)));
        Check("T 形中心格排除外角与底边中点",
            !tCenter.Contains(new Vector2(0f, 1f)) && !tCenter.Contains(new Vector2(1f, 1f))
            && !tCenter.Contains(new Vector2(0.5f, 1f)));

        // 10. 同一类型（同一 def 生成的多个实例，配置系统 ToItem 生成）可同时放置、互不覆盖、可单独拿起
        var same1 = new InventoryItem(PlacementShape.Rectangle(1, 1));
        var same2 = new InventoryItem(PlacementShape.Rectangle(1, 1));
        Check("同 Id 第一个放入", inv.TryPlace(same1, new Vector2I(7, 8)).Outcome == PlaceOutcome.Success);
        Check("同 Id 第二个放入(不再顶替)", inv.TryPlace(same2, new Vector2I(8, 8)).Outcome == PlaceOutcome.Success);
        Check("同 Id 两格各自独立占用",
            inv.GetItemAt(new Vector2I(7, 8)) == same1 && inv.GetItemAt(new Vector2I(8, 8)) == same2);
        Check("同 Id 容器登记两个实例",
            inv.PlacedItems.Count(i => i == same1 || i == same2) == 2);
        Check("同 Id 可单独拿起一个",
            inv.RemoveItem(same1) && inv.GetItemAt(new Vector2I(8, 8)) == same2);

        // 11. 旋转朝向计数：RotationTurns 与形状同步累加（渲染层据此旋转贴图内容，不重新拉伸）
        var r = new InventoryItem(PlacementShape.Line(3));
        Check("初始朝向 0", r.RotationTurns == 0);
        r.RotateClockwise();
        Check("转 1 次→竖条且朝向 1", r.RotationTurns == 1 && r.Shape.Height == 3);
        r.RotateClockwise();
        r.RotateClockwise();
        Check("再转 2 次→270°竖条且朝向 3", r.RotationTurns == 3 &&
              r.Shape.ToPattern().SequenceEqual(new[] { "X", "X", "X" }));
        r.RotateClockwise();
        Check("转满 4 次回到 0（模 4）", r.RotationTurns == 0 && r.Shape.ToPattern().SequenceEqual(new[] { "XXX" }));

        GD.Print(h2.Shape.ToPattern());
    }
}
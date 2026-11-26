using Godot;
using SaleFish.Main;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 背包测试关卡（InventoryDemo.tscn）测试：主关卡已把「获取放置物 / 丢弃放置物」迁移到这里。
///  ▪ 获取/丢弃按钮挂载、初始空手；
///  ▪ 点「获取」→ 手持物品、获取禁用、丢弃出现；手持时再点「获取」无效（保持原实例）；
///  ▪ 点「丢弃」→ 空手、按钮复位；
///  ▪ 宝箱仓库按钮存在（同 WarehouseButton 组件，开/关/拖拽的专属集成已在 TestMainStage 覆盖，这里不重复）。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestInventoryDemo.tscn --quit-after 240
/// </summary>
public sealed partial class TestInventoryDemo : Node
{
    private int _passed;
    private int _failed;

    private InventoryDemo _demo = null!;
    private Button _get = null!;
    private Button _discard = null!;
    private Sprite2D _inventory = null!;

    public override void _Ready()
    {
        _demo = ResourceLoader.Load<PackedScene>("res://Scenes/InventoryDemo.tscn").Instantiate<InventoryDemo>();
        AddChild(_demo);

        _get = FindButton("GetButton");
        _discard = FindButton("DiscardButton");
        _inventory = _demo.GetNodeOrNull<Sprite2D>("Inventory");

        Run();
        GD.Print($"[InvDemoTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[InvDemoTest] ALL PASSED" : "[InvDemoTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Run()
    {
        var hand = HeldItemController.Instance;
        Check("宝箱仓库按钮存在（Inventory，挂 WarehouseButton）", _inventory != null);
        Check("初始：获取可用、丢弃不出现", !_get.Disabled && !_discard.Visible);
        Check("初始：空手", hand != null && hand.HeldItem == null);

        Press(_get);
        var held = hand!.HeldItem;
        Check("点「获取」：手持一件物品", held != null);
        Check("获取后：获取禁用、丢弃出现", _get.Disabled && _discard.Visible);
        Check("获取后：物品有形状（占格）", held != null && held.Shape.CellCount > 0);

        Press(_get);
        Check("手持时再点「获取」：保持原实例（按钮已禁用，逻辑再兜底）",
            hand.HeldItem != null && ReferenceEquals(hand.HeldItem, held));

        Press(_discard);
        Check("点「丢弃」：回到空手", hand.HeldItem == null);
        Check("丢弃后：获取恢复可用、丢弃消失", !_get.Disabled && !_discard.Visible);
    }

    /// <summary>按名字找按钮（运行时代码创建的节点不被场景“拥有”，FindChild 需 owned=false）。</summary>
    private Button FindButton(string name)
    {
        var n = _demo.FindChild(name, recursive: true, owned: false);
        if (n is not Button b)
        {
            GD.PrintErr($"[InvDemoTest] 找不到按钮 {name}");
            GetTree().Quit(1);
            return null!;
        }
        return b;
    }

    /// <summary>派发按钮 pressed 信号（headless 不经输入队列）。</summary>
    private static void Press(Button? b)
    {
        if (b == null || !GodotObject.IsInstanceValid(b)) return;
        b.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void Check(string name, bool cond) => Check(name, cond, null);

    private void Check(string name, bool cond, string? detail)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
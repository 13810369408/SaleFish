using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// 水中参照小点（WaterDots）测试：验证默认参数、斑点全部落在水域矩形内（四周内缩 Radius，不被水边截断）、
/// 数量可控、固定种子布局可复现（换种子布局不同）、无矩形碰撞体时安全不画。
/// 测试不实例化正式关卡，自己造一个带矩形碰撞体的 Water(Area2D) 挂 WaterDots 子节点。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestWaterDots.tscn --quit-after 240
/// </summary>
public sealed partial class TestWaterDots : Node
{
    private int _passed;
    private int _failed;

    private Area2D _water = null!;
    private CollisionShape2D _cs = null!;
    private Rect2 _waterRect;

    public override void _Ready()
    {
        _water = new Area2D { Name = "Water", GlobalPosition = new Vector2(500, 200) };
        _cs = new CollisionShape2D { Name = "CollisionShape2D" };
        _cs.Shape = new RectangleShape2D { Size = new Vector2(1000, 400) };
        _water.AddChild(_cs);
        AddChild(_water);
        _waterRect = new Rect2(_cs.GlobalPosition - new Vector2(500, 200), new Vector2(1000, 400));

        var dots = new WaterDots { Name = "WaterDots" };
        _water.AddChild(dots);   // WaterDots._Ready：读父碰撞体 → 布局

        Run(dots);
        GD.Print($"[WaterDotsTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[WaterDotsTest] ALL PASSED" : "[WaterDotsTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    private void Run(WaterDots dots)
    {
        Check("默认数量 = 120", dots.Count == 120);
        Check("默认半径 = 5", Mathf.Abs(dots.Radius - 5f) < 0.001f);
        Check("默认半透明白", dots.DotsColor.A < 1f && dots.DotsColor.R == 1f && dots.DotsColor.G == 1f);

        Check("生成了 120 个点", dots.Points.Length == 120);

        // 全部落在水域矩形内、且四周内缩半径（圆不会被水边截断）
        bool allInside = true;
        foreach (var p in dots.Points)
        {
            if (p.X < _waterRect.Position.X + dots.Radius || p.X > _waterRect.Position.X + _waterRect.Size.X - dots.Radius ||
                p.Y < _waterRect.Position.Y + dots.Radius || p.Y > _waterRect.Position.Y + _waterRect.Size.Y - dots.Radius)
            {
                allInside = false;
                break;
            }
        }
        Check("全部斑点在水域矩形内（内缩半径不被截断）", allInside);

        // 固定种子可复现：重算同种子 → 布局一致；换种子 → 布局不同
        var first = dots.Points;
        dots.Seed = 20261028;
        dots.Recompute();
        bool sameSeedSame = dots.Points.Length == 120;
        for (int i = 0; i < dots.Points.Length; i++)
        {
            if (dots.Points[i].DistanceTo(first[i]) > 0.001f) sameSeedSame = false;
        }
        Check("同种子重算 → 布局逐点一致（可复现参照系）", sameSeedSame);

        dots.Seed = 999;
        dots.Recompute();
        bool differentSeed = false;
        for (int i = 0; i < Mathf.Min(dots.Points.Length, first.Length); i++)
        {
            if (dots.Points[i].DistanceTo(first[i]) > 1f) differentSeed = true;
        }
        Check("换种子 → 布局变化", differentSeed);

        // 无碰撞体：安全返回 0 点不崩
        var orphan = new WaterDots();
        AddChild(orphan);   // 无父碰撞体 → 警告 + 空点
        Check("无矩形碰撞体时点集为空（不崩）", orphan.Points.Length == 0);

        orphan.QueueFree();
        _water.QueueFree();
    }
}
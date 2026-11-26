using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// 挣扎 demo（FishStruggleDemo）集成验证：加载带 Sartine FishDef 的 demo 实例，
/// 检查：鱼生成且朝右（Scale.X 负）、动画初始 NormalPlaySpeed；internal 触发开始挣扎
/// 后：FishDef 上的特效+震屏走 StruggleFeedback 无异常、动画切 StrugglePlaySpeed、
/// 位置随 MoveSpeed 向右推进（Struggling=true 期间 x 增长）；停止挣扎后：动画回 Normal、
/// 鱼与镜头归位。结束后 Quit(0)。运行方式：godot --headless --path &lt;project&gt;
/// res://Scenes/TestFishStruggleDemo.tscn --quit-after 240
/// </summary>
public sealed partial class TestFishStruggleDemo : Node
{
    /// <summary>被验证的 demo 根节点路径（场景里名为 Demo）。</summary>
    [Export] public NodePath DemoPath = new("Demo");

    private Main.FishStruggleDemo _demo = null!;
    private int _frames;
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        _demo = GetNodeOrNull<Main.FishStruggleDemo>(DemoPath)!;
        if (_demo == null) { GD.PushError("[FishStruggleDemoVerify] Demo node not found"); GetTree().Quit(1); return; }
        GD.Print("[FishStruggleDemoVerify] ready, waiting for demo _Ready...");
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == 10)   // 等 demo 生成鱼 + UI 构建完
        {
            var fish = _demo.Fish;
            Check("配置了 FishDef → demo 生成了鱼", fish != null);
            if (fish == null) { Finish(); return; }

            var sprite = fish.GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D");
            Check("鱼带 AnimatedSprite2D", sprite != null);
            if (sprite != null)
            {
                Check("动画初始为 NormalPlaySpeed", sprite.SpeedScale == (fish.Def?.NormalPlaySpeed ?? 1f));
                Check("鱼头朝右 = 精灵 Scale.X 为负（镜像）", Mathf.Abs(sprite.Scale.X + fish.Def?.SpriteScale ?? 0f) < 0.001f);
            }

            var startX = fish.GlobalPosition.X;
            _demo.StartStruggle();                                   // 内部入口：特效+震屏+切动画
            Check("开始挣扎后 Struggling=true", _demo.Struggling);
            if (sprite != null)
                Check("动画切到 StrugglePlaySpeed", sprite.SpeedScale == (fish.Def?.StrugglePlaySpeed ?? 1f));
            Check("开始挣扎初始 x = 0（默认 MoveSpeed 右移起点）", startX == 0f);
        }

        if (_frames is > 10 and <= 40)
        {
            var fish = _demo.Fish;
            if (_demo.Struggling && fish != null && _frames == 35)
                Check($"挣扎期间 x 向右推进（{_demo.MoveSpeed:0} px/s × ~0.4s → x={fish.GlobalPosition.X:F1}）",
                    fish.GlobalPosition.X > 0f);
        }

        if (_frames == 45)
        {
            _demo.StopStruggle();
            var fish = _demo.Fish;
            Check("停止后 Struggling=false", !_demo.Struggling);
            if (fish != null)
            {
                Check("鱼回到原点", fish.GlobalPosition == Vector2.Zero);
                var sprite = fish.GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D");
                if (sprite != null) Check("动画回 NormalPlaySpeed", sprite.SpeedScale == (fish.Def?.NormalPlaySpeed ?? 1f));
            }
            var cam = _demo.GetNodeOrNull<Camera2D>("Camera2D");
            if (cam != null) Check("镜头回到原点", cam.GlobalPosition == Vector2.Zero);
            Finish();
        }
    }

    private void Finish()
    {
        GD.Print($"[FishStruggleDemoVerify] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishStruggleDemoVerify] ALL PASSED" : "[FishStruggleDemoVerify] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
using Godot;
using SaleFish.Fx;

namespace SaleFish.Test;

/// <summary>
/// RingBurstEffect 双延迟验证（临时）：给 ExpandingRing 与 RadialBurst 各自喂不同 RingDelay/BurstDelay，
/// 逐帧断言对应 GPU 粒子是否按「Play() 起计」到点才 Emitting，以及 0 延迟 = 同帧立即发动。
/// 纯时序验证，不依赖效果可见性。
/// 运行：godot --headless --path &lt;project&gt; res://Scenes/TestRingBurstDelay.tscn --quit-after 600
/// </summary>
public sealed partial class TestRingBurstDelay : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        Run();
        GD.Print($"[RingBurstDelayTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[RingBurstDelayTest] ALL PASSED" : "[RingBurstDelayTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Run()
    {
        // 场景树里父节点不变：反复 new + AddChild 来测不同参数组合
        // （每个效果都本地坐标、用自带真节点名，不依赖外部 prefab）

        // ---- 1) RingDelay=0.5 / BurstDelay=0.5：两边都在 0.25s 时未发动、0.75s 时都已发动 ----
        var fx1 = MakeFx(0.5f, 0.5f);
        fx1.Play();
        for (var i = 0; i < 15; i++) fx1.Step(1f / 60f);          // ~0.25s
        Check("Ring 0.5s 延迟：0.25s 时还未 Emission", RingIsEmitting(fx1) == false);
        Check("Burst 0.5s 延迟：0.25s 时还未 Emitting", BurstIsEmitting(fx1) == false);
        for (var i = 0; i < 31; i++) fx1.Step(1f / 60f);          // 再 ~0.52s → 累计 ~0.77s
        Check("Ring 0.5s 延迟：~0.77s 时已 Emitting", RingIsEmitting(fx1));
        Check("Burst 0.5s 延迟：~0.77s 时已 Emitting", BurstIsEmitting(fx1));

        // ---- 2) RingDelay=0.9 / BurstDelay=0.1：0.15s 时 ring 未发、burst 已发 ----
        var fx2 = MakeFx(0.9f, 0.1f);
        fx2.Play();
        for (var i = 0; i < 9; i++) fx2.Step(1f / 60f);           // ~0.15s
        Check("Ring 0.9s：0.15s target 未发", RingIsEmitting(fx2) == false);
        Check("Burst 0.1s：0.15s 已 Emitting", BurstIsEmitting(fx2));

        // ---- 3) RingDelay=0 / BurstDelay=0：同帧立即发动 ----
        var fx3 = MakeFx(0f, 0f);
        fx3.Play();
        Check("双 0 延迟：Ring 同帧 Emitting", RingIsEmitting(fx3));
        Check("双 0 延迟：Burst 同帧 Emitting", BurstIsEmitting(fx3));

        // ---- 4) RingDelay=.2 / BurstDelay=2：0.3s 时 ring 已发、burst 未发 ----
        var fx4 = MakeFx(0.2f, 2f);
        fx4.Play();
        for (var i = 0; i < 18; i++) fx4.Step(1f / 60f);          // ~0.3s
        Check("Ring .2s：0.3s 已发", RingIsEmitting(fx4));
        Check("Burst 2s：0.3s 未发", BurstIsEmitting(fx4) == false);

        // ---- 5) 复播清场（模拟池复用）：先发一轮（粒子已飞出）→ 不经过 Stop 直接带延迟重播。
        //    Play() 开头 Restart 清掉上一轮已发射粒子 → 等待期两子特效均为空白（Emitting=false），
        //    到点才逐个发。能捕获「复用时旧残影闪现」回归：旧版重播等待期旧粒子还在飞。 ----
        var fx5 = MakeFx(0f, 0f);
        fx5.Play();
        Check("复播前：首轮双 0 同帧 Emitting", RingIsEmitting(fx5) && BurstIsEmitting(fx5));
        for (var i = 0; i < 10; i++) fx5.Step(1f / 60f);          // 首轮粒子发射出一段时间
        fx5.RingDelay = 0.5f;
        fx5.BurstDelay = 0.5f;
        fx5.Play();                                               // 池复用式重播（未 Stop）
        Check("复播清场：重播同帧两子特效均复位为未 Emitting", RingIsEmitting(fx5) == false && BurstIsEmitting(fx5) == false);
        for (var i = 0; i < 15; i++) fx5.Step(1f / 60f);          // 等待期 ~0.25s
        Check("复播清场：0.25s 等待期仍空白（旧残影已清）", RingIsEmitting(fx5) == false && BurstIsEmitting(fx5) == false);
        for (var i = 0; i < 31; i++) fx5.Step(1f / 60f);          // 再 ~0.52s → ~0.77s 已过延迟
        Check("复播清场：~0.77s 两子特效按新延迟依次发动", RingIsEmitting(fx5) && BurstIsEmitting(fx5));

        // ---- 6) Stop 清场：播到一半 Stop → 两子特效停且复位（不再飞旧粒子）----
        var fx6 = MakeFx(0.2f, 0.2f);
        fx6.Play();
        for (var i = 0; i < 20; i++) fx6.Step(1f / 60f);          // ~0.33s：两特效都已发动
        Check("Stop 前：双特效已发动", RingIsEmitting(fx6) && BurstIsEmitting(fx6));
        fx6.Stop();
        Check("Stop 清场：两子特效立即停且复位为未 Emitting", RingIsEmitting(fx6) == false && BurstIsEmitting(fx6) == false);
        for (var i = 0; i < 10; i++) fx6.Step(1f / 60f);
        Check("Stop 后继续推进：保持空白（旧粒子被清）", RingIsEmitting(fx6) == false && BurstIsEmitting(fx6) == false);

        foreach (var fx in new[] { fx1, fx2, fx3, fx4, fx5, fx6 }) fx.QueueFree();
    }

    private RingBurstEffect MakeFx(float ringDelay, float burstDelay)
    {
        var fx = new RingBurstEffect { RingDelay = ringDelay, BurstDelay = burstDelay };
        // 手动建两个 GPU 粒子子节点（真实的 GPUParticles2D 节点即可测试 Emitting 状态切换）
        var ring = new GpuParticles2D { Name = "ExpandingRing", Emitting = false, Lifetime = 1f };
        fx.AddChild(ring);
        var burst = new GpuParticles2D { Name = "RadialBurst", Emitting = false, Lifetime = 1f };
        fx.AddChild(burst);
        AddChild(fx);   // 进树时 _Ready 自动执行（GetNodeOrNull 解析子节点）
        return fx;
    }

    private static bool RingIsEmitting(RingBurstEffect fx)
        => fx.GetNodeOrNull<GpuParticles2D>("ExpandingRing")?.Emitting ?? false;

    private static bool BurstIsEmitting(RingBurstEffect fx)
        => fx.GetNodeOrNull<GpuParticles2D>("RadialBurst")?.Emitting ?? false;

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
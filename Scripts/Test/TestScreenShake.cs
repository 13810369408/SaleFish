using System;
using Godot;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 震屏组件自动化测试（覆盖「有限时长 + Ease 减弱收尾」与「无限循环」两种语义）：
///  ▪ 六档内置预设（ShakePresets.All）：数量 6、强度单调升序（振幅/滚动逐档加大、频率逐档放低）、
///    每档有时长默认值 + 默认 Ease=OutCubic（时长按场景可自由覆盖、不要求排序）；
///  ▪ 缓动库 ShakeEases.Evaluate：全曲线端点恒 0/1、Linear/OutCubic/OutBounce 已知值、全程无 NaN/Inf；
///  ▪ 包络 ScreenShaker.Envelope：t=0 恒 1、t=Duration 恒 0、OutCubic 单调递减、Duration=0 恒 1、
///    EnableEase=false 时退化为直线衰减（Envelope=1−t，无缓动）；
///  ▪ 纯函数 Sample：确定性、有界（有限档全程 |偏移| ≤ Amplitude）、到点即 (0,0)、不同 seed 结果不同、
///    改 Ease 会真正改变减弱形状（克隆预设验证，不污染共享档位）；
///  ▪ 实例接入真实相机：Play → IsShaking/Active/Elapsed 正确、有限档**到时自动停**（相机归位、Elapsed 回 0）、
///    无限档（Duration=0）连步进 5s 不自动停、半途替换重置计时、Stop() 立刻归位；
///  ▪ 链式 API：SetEase/SetDuration 返回自身并生效；
///  ▪ 登记表：挂在 Camera2D 子树的实例自动解析到相机、Primary 取第一个、离场注销。
///
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestScreenShake.tscn --quit-after 240
/// 断言风格与 TestFishing 等一致：PASS/FAIL 打点 + 汇总计数，0 失败则退出码 0。
/// </summary>
public sealed partial class TestScreenShake : Node
{
    private int _passed;
    private int _failed;

    private readonly Camera2D _camera = new() { Name = "TestCamera" };
    private readonly ScreenShaker _shaker = new() { Name = "TestShaker" };

    private const uint SeedA = 12345u;
    private const uint SeedB = 67890u;
    private const double Dt = 1.0 / 60.0;

    public override void _Ready()
    {
        // 组装一棵真实树：Camera2D > ScreenShaker（ScreenShaker 自动从父级解析到相机、登记 Primary）
        AddChild(_camera);
        _camera.AddChild(_shaker);
        _shaker.Seed = SeedA;                                   // 固定种子：震屏全程确定性
        _shaker.ProcessMode = ProcessModeEnum.Disabled;         // 关掉引擎自动推进，全部手动 Step

        Run();
        GD.Print($"[ScreenShakeTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[ScreenShakeTest] ALL PASSED" : "[ScreenShakeTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Run()
    {
        Check("登记表：初始 1 个实例", ScreenShaker.InstanceCount == 1);
        Check("Primary = 场景第一个实例", ReferenceEquals(ScreenShaker.Primary, _shaker));
        Check("挂在相机下自动解析到相机（无需 CameraPath）", _camera.Offset == Vector2.Zero);

        // ---- 1. 六档内置预设：轻微 → 极端 强度单调 ----
        var all = ShakePresets.All;
        Check("内置预设共 6 档", all.Length == 6);
        for (var i = 1; i < all.Length; i++)
        {
            var prev = all[i - 1];
            var cur = all[i];
            Check($"档位 {i} {cur.DisplayName}：振幅递增（{prev.Amplitude}→{cur.Amplitude}）", cur.Amplitude > prev.Amplitude);
            Check($"档位 {i}：频率递减（{prev.Frequency}→{cur.Frequency}）", cur.Frequency < prev.Frequency);
            Check($"档位 {i}：滚动不递减（{prev.RotationDegrees}→{cur.RotationDegrees}）", cur.RotationDegrees >= prev.RotationDegrees);
            // 时长/减弱曲线都是用户可自由覆盖的手感参数：不要求任何排序（轻微档时长达可大于下一档）
            Check($"档位 {i}：Ease=默认 OutCubic", cur.Ease == ShakeEase.OutCubic);
            Check($"档位 {i}：Ease 默认开启（EnableEase=true）", cur.EnableEase);
        }
        for (var i = 0; i < all.Length; i++)
        {
            var p = all[i];
            Check($"档位 {i + 1} 有名字", !string.IsNullOrWhiteSpace(p.DisplayName));
            Check($"档位 {i + 1} 带默认为有时长（Duration>0）", p.Duration > 0f);
        }

        // ---- 2. 缓动库：全曲线端点恒 0/1、已知值、无 NaN/Inf；链式 API ----
        var eases = Enum.GetValues<ShakeEase>();
        Check("ShakeEase 共 31 种曲线（Linear + 30 个 In/Out/InOut）", eases.Length == 31);
        var endpointsOk = true;
        var finiteOk = true;
        var probes = new[] { 0f, 0.3f, 0.5f, 0.8f, 1f };
        foreach (var e in eases)
        {
            endpointsOk &= Near(ShakeEases.Evaluate(e, 0f), 0f);
            endpointsOk &= Near(ShakeEases.Evaluate(e, 1f), 1f);
            // 0 / 0.3 / 0.5 / 0.8 / 1 处求值绝不 NaN/Inf（Elastic/Expo/Bounce 分段式最容易踩 0 除/负底）
            foreach (var t in probes)
            {
                var v = ShakeEases.Evaluate(e, t);
                finiteOk &= !float.IsNaN(v) && !float.IsInfinity(v);
            }
        }
        Check("全曲线端点：Evaluate(·,0)=0、Evaluate(·,1)=1", endpointsOk);
        Check("全曲线 5 点采样无 NaN/Inf", finiteOk);
        Check("Linear 中点 = 0.5", Near(ShakeEases.Evaluate(ShakeEase.Linear, 0.5f), 0.5f));
        Check("OutCubic 中点 = 0.875（1−(1−0.5)³）", Near(ShakeEases.Evaluate(ShakeEase.OutCubic, 0.5f), 0.875f));
        Check("InOutQuad 中点 = 0.5", Near(ShakeEases.Evaluate(ShakeEase.InOutQuad, 0.5f), 0.5f));
        Check("OutBounce 中点 = 0.765625（第二反弹段 n1(t−1.5/d1)t+0.75）", Near(ShakeEases.Evaluate(ShakeEase.OutBounce, 0.5f), 0.765625f));

        // 链式 API：返回自身、字段生效（DOTween .SetEase 风格）
        var chained = new ScreenShakePreset();
        Check("SetEase 返回自身", ReferenceEquals(chained, chained.SetEase(ShakeEase.OutBack)));
        Check("SetEase 已生效（Ease=OutBack）", chained.Ease == ShakeEase.OutBack);
        Check("SetDuration 返回自身", ReferenceEquals(chained, chained.SetDuration(1.2f)));
        Check("SetDuration 已生效（Duration=1.2）", Near(chained.Duration, 1.2f));

        // ---- 3. 包络 Envelope：形状 / 端点 / 单调 / 无限恒 1 ----
        var m = ShakePresets.Medium;   // Duration=0.35、Ease=OutCubic
        Check("Envelope(t=0) = 1（满幅开局）", Near(ScreenShaker.Envelope(m, 0f), 1f));
        Check($"Envelope(t=Duration={m.Duration}) = 0（柔和地减到 0）", Near(ScreenShaker.Envelope(m, m.Duration), 0f));
        Check("Envelope 卡在时长后仍 = 0（t 超过 Duration）", Near(ScreenShaker.Envelope(m, m.Duration * 2f), 0f));
        Check("Envelope 中点 = OutCubic 复合值（1−0.5）³ = 0.125", Near(ScreenShaker.Envelope(m, m.Duration * 0.5f), 0.125f));

        var monoOk = true;
        var prevEnv = ScreenShaker.Envelope(m, 0f);
        for (var t = 0.02f; t <= m.Duration; t += 0.02f)
        {
            var env = ScreenShaker.Envelope(m, t);
            monoOk &= env <= prevEnv + 1e-6f;   // OutCubic 减弱曲线单调递减（不反弹）
            prevEnv = env;
        }
        Check("OutCubic 减弱全程单调递减（先猛后轻、不回头）", monoOk);

        var inf = new ScreenShakePreset { Duration = 0f, Ease = ShakeEase.OutCubic };
        Check("无限档（Duration=0）包络恒 = 1", Near(ScreenShaker.Envelope(inf, 0f), 1f)
            && Near(ScreenShaker.Envelope(inf, 30f), 1f));

        // ---- 3b. EnableEase = false：没有缓动，收尾退化成直线衰减（1−t） ----
        var lin = new ScreenShakePreset { Duration = 0.4f, Ease = ShakeEase.OutCubic, EnableEase = false };
        Check("关 ease：Envelope(t=0) = 1", Near(ScreenShaker.Envelope(lin, 0f), 1f));
        Check("关 ease：Envelope ¼ = 0.75（直线）", Near(ScreenShaker.Envelope(lin, 0.1f), 0.75f));
        Check("关 ease：Envelope 中点 = 0.5（直线）", Near(ScreenShaker.Envelope(lin, 0.2f), 0.5f));
        Check("关 ease：Envelope ¾ = 0.25（直线）", Near(ScreenShaker.Envelope(lin, 0.3f), 0.25f));
        Check("关 ease：Envelope 终点 = 0", Near(ScreenShaker.Envelope(lin, 0.4f), 0f));
        Check("关 ease：Envelope 超时仍 = 0", Near(ScreenShaker.Envelope(lin, 0.8f), 0f));
        var onEase = new ScreenShakePreset { Duration = 0.4f, Ease = ShakeEase.OutCubic, EnableEase = true };
        Check("开 ease 同点 Envelope = 0.125（OutCubic 对照，关 ease 是 0.5）", Near(ScreenShaker.Envelope(onEase, 0.2f), 0.125f));
        Check("开/关 ease 在同一时刻走的是不同衰减（结果不同）", !Near(ScreenShaker.Envelope(onEase, 0.2f), ScreenShaker.Envelope(lin, 0.2f)));
        var infLin = new ScreenShakePreset { Duration = 0f, EnableEase = false };
        Check("关 ease 且无限档：包络恒 1（Duration=0 优先）", Near(ScreenShaker.Envelope(infLin, 3f), 1f));
        var linBounds = true;
        for (var t = 0f; t < 0.399f; t += 0.01f)
            linBounds &= ScreenShaker.Sample(lin, SeedA, t).Offset.Length() <= 12f + 0.01f;   // 默认 Amplitude=12
        Check("关 ease：Sample 全程有界且到点归零",
            linBounds && ScreenShaker.Sample(lin, SeedA, 0.4f) == (Vector2.Zero, 0f));

        // ---- 4. 纯函数 Sample：确定性 / 有界 / 到点归零 / Ease 真改变形状 ----
        Check("Sample(null) = (0,0)", ScreenShaker.Sample(null, SeedA, 0.1f) == (Vector2.Zero, 0f));

        // 有限档：全程（开头 → 临近终点都在减弱）|偏移| ≤ 振幅，且确实在动
        var maxMag = 0f;
        var inBounds = true;
        for (var t = 0f; t < m.Duration; t += m.Duration / 30f)
        {
            var (off, roll) = ScreenShaker.Sample(m, SeedA, t);
            inBounds &= off.Length() <= m.Amplitude + 0.01f;
            inBounds &= Mathf.Abs(roll) <= m.RotationDegrees + 0.01f;
            maxMag = Mathf.Max(maxMag, off.Length());
        }
        Check("有限档全程有界（|偏移| ≤ 振幅、|滚动| ≤ 滚动值）", inBounds);
        Check($"有限档确实在动（期间最大位移 {maxMag:F1} ≥ 30% 振幅）", maxMag >= m.Amplitude * 0.3f);
        Check("有限档到点后 Sample = (0,0)", ScreenShaker.Sample(m, SeedA, m.Duration) == (Vector2.Zero, 0f));
        Check("有限档超时后 Sample = (0,0)", ScreenShaker.Sample(m, SeedA, 5f) == (Vector2.Zero, 0f));

        // 无限档：5s 后仍满幅动（旧版语义保留）
        var (offInf, _) = ScreenShaker.Sample(inf, SeedA, 5f);
        Check($"无限档 5s 后仍在满幅动（|offset|={offInf.Length():F1}）", offInf.Length() > 0f);

        // 滚动：仅当 RotationDegrees > 0 才有非零滚动；纯平移档恒 0
        var (_, r0) = ScreenShaker.Sample(ShakePresets.Light, SeedA, 0.05f);
        var (o1, r1) = ScreenShaker.Sample(ShakePresets.Violent, SeedA, 0.05f);
        Check("纯平移档滚动 = 0", r0 == 0f);
        Check($"带滚动档出现非零滚动（Light 0° vs Violent {r1:F2}°）", Mathf.Abs(r1) > 0f);
        Check("Light 有位移（轻微也动）", o1.Length() > 0f || ScreenShaker.Sample(ShakePresets.Light, SeedA, 0.1f).Offset.Length() > 0f);

        // 确定性：同 seed 两遍完全相同；不同 seed 结果至少一处不同
        var det = true;
        var diff = false;
        for (var i = 1; i < 30; i++)
        {
            var t = m.Duration * i / 30f;
            var s1 = ScreenShaker.Sample(m, SeedA, t);
            var s2 = ScreenShaker.Sample(m, SeedA, t);
            det &= s1 == s2;
            var s3 = ScreenShaker.Sample(m, SeedB, t);
            diff |= s1.Offset != s3.Offset || s1.Roll != s3.Roll;
        }
        Check("确定性：同 (preset, seed, t) 结果一致", det);
        Check("不同 seed 扰出不同震屏", diff);

        // Ease 换曲线会真的改变减弱形状：同 seed 同时刻，OutCubic vs OutBack 末端 0.7×Duration 处结果不同。
        // 用克隆预设（改 Ease 绝不动共享的 ShakePresets.Medium）。
        var mBack = new ScreenShakePreset
        {
            Amplitude = m.Amplitude, Frequency = m.Frequency, RotationDegrees = m.RotationDegrees,
            Duration = m.Duration, Ease = ShakeEase.OutBack,
        };
        var endT = m.Duration * 0.7f;
        var eCubic = ScreenShaker.Sample(m, SeedA, endT).Offset;
        var eBack = ScreenShaker.Sample(mBack, SeedA, endT).Offset;
        Check("同点不同 Ease：减弱曲线不同 → 采样结果不同（OutCubic≈0 正幅 vs OutBack 过冲负幅）", eCubic != eBack);

        // ---- 5. 实例接入真实相机：有限档到时自动停 / 无限档不自动停 / 替换 / Stop ----
        Check("初始：不在震、相机偏移为 0", !_shaker.IsShaking && _camera.Offset == Vector2.Zero);

        _shaker.Play(ShakePresets.Medium);
        Check("Play 后：IsShaking、Active = 预设、Elapsed≈0", _shaker.IsShaking && _shaker.Active == ShakePresets.Medium && _shaker.Elapsed < 0.02f);

        var applied = _shaker.Step(Dt);
        Check("Step 后：相机偏移 = 返回值", _camera.Offset == applied);
        Check($"Step 后：已发生位移（|offset|={applied.Length():F1}）", applied.Length() > 0f);

        // 有限档：步进到自动停。停前最后一帧（非零的一帧）位移已经按包络缩得很小、没有突兀切 0
        var steps = 0;
        var lastMove = Vector2.Zero;
        while (_shaker.IsShaking && steps < 100)
        {
            var s = _shaker.Step(Dt);
            if (s.Length() > 0f) lastMove = s;   // 跨越帧 Sample 已返 (0,0)：跟踪最后一帧真位移
            steps++;
        }
        var expectedSteps = Mathf.CeilToInt(ShakePresets.Medium.Duration / (float)Dt);
        Check($"有限档到时自动停（{steps} 步 ≈ 期望 {expectedSteps} 步，容差 ±2）",
            steps >= expectedSteps - 1 && steps <= expectedSteps + 2);
        Check("自动停后：IsShaking=false、Active=null", !_shaker.IsShaking && _shaker.Active == null);
        Check("自动停后：相机偏移归零、滚动恢复 0°", _camera.Offset == Vector2.Zero && Mathf.Abs(_camera.RotationDegrees) < 1e-6f);
        Check("自动停后：Elapsed 回 0", _shaker.Elapsed == 0f);
        Check($"停前末段位移受限（|lastMove|={lastMove.Length():F3} ∈ (0, 30% 振幅)）",
            lastMove.Length() > 0f && lastMove.Length() < ShakePresets.Medium.Amplitude * 0.3f);

        // 无限档：连步进 300 帧（=5 秒）也不自动停、Elapsed 持续累计
        _shaker.Play(new ScreenShakePreset { Amplitude = 12f, Frequency = 26f, Duration = 0f });
        for (var i = 0; i < 300; i++) _shaker.Step(Dt);
        Check($"无限档 Step 300 帧（5s）后仍在震（Duration=0 不自动停，仅 Stop() 终止）", _shaker.IsShaking);
        Check("无限档 Elapsed 持续累计（>5s）", _shaker.Elapsed > 5f);
        _shaker.Stop();
        Check("Stop()：IsShaking=false、相机偏移归零、滚动恢复 0°",
            !_shaker.IsShaking && _camera.Offset == Vector2.Zero && Mathf.Abs(_camera.RotationDegrees) < 1e-6f);
        Check("停后：Elapsed 回 0", _shaker.Elapsed == 0f);

        // 带滚动档：播放期间滚动非零、结束后恢复
        _shaker.Play(ShakePresets.Extreme);
        _shaker.Step(Dt);
        Check("极端档播放中：滚动非零", Mathf.Abs(_camera.RotationDegrees) > 0f);
        _shaker.Stop();
        Check("再次 Stop()：相机归零", !_shaker.IsShaking && _camera.Offset == Vector2.Zero && Mathf.Abs(_camera.RotationDegrees) < 1e-6f);

        // 半途替换：新预设重新从 0 计（中途切档立即换手感、计时重来）
        _shaker.Play(ShakePresets.Light);
        _shaker.Step(Dt);
        _shaker.Play(ShakePresets.Violent);
        Check("替换：Active 换成新预设、计时重来（Elapsed≈0）", _shaker.IsShaking && ReferenceEquals(_shaker.Active, ShakePresets.Violent) && _shaker.Elapsed < 0.02f);
        _shaker.Stop();

        // ---- 6. 登记表：加第二个实例 → Primary 仍是第一个；离场注销 ----
        var second = new ScreenShaker { Name = "SecondShaker" };
        _camera.AddChild(second);
        Check("登记：第二个实例入表（计数 2）", ScreenShaker.InstanceCount == 2);
        Check("登记：Primary 仍是第一个", ReferenceEquals(ScreenShaker.Primary, _shaker));
        second.GetParent()!.RemoveChild(second);
        second.Free();
        Check("离场注销：计数回 1", ScreenShaker.InstanceCount == 1);
        Check("离场注销：Primary 不变", ReferenceEquals(ScreenShaker.Primary, _shaker));
    }

    /// <summary>浮点近似比较（避开 Mathf.IsEqualApprox 无容差重载的版本差异，统一绝对差）。</summary>
    private static bool Near(float a, float b, float eps = 1e-5f) => Mathf.Abs(a - b) <= eps;

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
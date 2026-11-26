using System.Collections.Generic;
using Godot;

namespace SaleFish.View;

/// <summary>
/// 震屏组件（Node2D）：挂在 Camera2D 下（或设 CameraPath 指过去）。任意时刻 Play 一份
/// ScreenShakePreset，通过修改相机的 Offset（平移）与 RotationDegrees（滚动）来震屏——
/// 只动 Offset、不碰 Position，因此能和主关卡的镜头跟随（MainStage 每帧写 camera.Position）无缝叠加。
///
/// 播放语义取决于预设的 Duration（见 ScreenShakePreset）：
///  ▪ Duration > 0（有限）：播够时长自动停止，末尾按预设的 Ease 曲线逐渐减弱收尾（Envelope = 1 − Ease(已播/总时长)）；
///  ▪ Duration = 0（无限循环）：恒定满幅持续晃动，只有手动 Stop()（或用另一份 Play 替换）才停止。
///
/// 用法：
///  场景里放一个本节点（惯例 Camera2D/ScreenShaker），任意代码调
///      GetNode&lt;ScreenShaker&gt;("Camera2D/ScreenShaker").Play(ShakePresets.Violent);
///  或走便利静态句柄 ScreenShaker.Primary（本域第一个登记实例），跨场景也能触发。
///
/// 帧驱动：ProcessMode 正常时 _Process 每帧自动推进；自动化测试把 ProcessMode 设为 Disabled 后
/// 手动调 Step(delta) —— 与钩子（Hook）/主关卡（MainStage）同款「公开步进」约定，全确定性。
///
/// 效果采样是无状态的纯函数 ScreenShaker.Sample(preset, seed, elapsed) → (偏移, 滚动)：
/// 同一 (preset, seed, elapsed) 永远返回同一值，直接可单测（见 scripts/test/TestScreenShake.cs）。
/// </summary>
public sealed partial class ScreenShaker : Node2D
{
    /// <summary>相机节点路径（相对本节点）；null = 自动解析：先看自己，再一路向上找父级里的 Camera2D
    /// （惯例把本节点放在 Camera2D 的直接子节点，两个都用得到）。</summary>
    [Export] public NodePath? CameraPath;

    /// <summary>随机种子：同一 Seed + 同一预设 + 同一时间 → 完全相同的震屏（自动化测试断言用）。
    /// 0 = _Ready 时随机化（默认，每次点击手感不同）。</summary>
    [Export] public uint Seed;

    // ---- 本域登记表：多场景同时存在也能精确取到「场景里第一个」，_EnterTree 登记、_ExitTree 注销 ----
    private static readonly List<ScreenShaker> Instances = [];

    /// <summary>场景里第一个登记的 ScreenShaker（最简单的触发入口：ScreenShaker.Primary?.Play(preset)）。</summary>
    public static ScreenShaker? Primary => Instances.Count > 0 ? Instances[0] : null;

    /// <summary>当前登记数（测试断言用：加场、离场增减）。</summary>
    public static int InstanceCount => Instances.Count;

    private Camera2D? _camera;
    private ScreenShakePreset? _preset;   // 当前播放的预设；null = 不在震
    private float _elapsed;               // 已播放时长（秒），兼作噪声相位推进用；未震恒 0
    private float _restRotation;          // 开始震时相机的基础旋转（停震恢复用，避免覆盖镜头原有角度）

    public override void _Ready()
    {
        if (Seed == 0) Seed = (uint)new RandomNumberGenerator().Randi();
        Instances.Remove(this);
        Instances.Add(this);
        _camera = ResolveCamera();
        if (_camera == null)
            GD.PushWarning("[ScreenShaker] 找不到限定的相机：震屏将不生效（配置 CameraPath，或把本节点放进 Camera2D 下）。");
    }

    public override void _ExitTree()
    {
        Stop();                // 恢复相机（避免中途卸载场景把镜头留在偏移位/转过的角度）
        Instances.Remove(this);
    }

    public override void _Process(double delta)
    {
        Step(delta);           // ProcessMode=Disabled 时引擎不调用，测试就手动 Step
    }

    /// <summary>是否正在震屏。</summary>
    public bool IsShaking => _preset != null;

    /// <summary>当前播放的预设（null = 不在震）。</summary>
    public ScreenShakePreset? Active => _preset;

    /// <summary>本次已播放时长（秒，未震恒 0；无限循环中只会越长，不设上限）。</summary>
    public float Elapsed => _elapsed;

    /// <summary>开始播放一份震屏预设（若正在播则直接被替换）。preset 为 null 时忽略。
    /// 播放时长/减弱曲线由预设决定：Duration&gt;0 到时自动停，Duration=0 等 Stop()。</summary>
    public void Play(ScreenShakePreset preset)
    {
        if (preset == null) return;
        _preset = preset;
        _elapsed = 0f;
        _restRotation = _camera?.RotationDegrees ?? 0f;
    }

    /// <summary>停止震屏：相机恢复原位置/角度。</summary>
    public void Stop()
    {
        _preset = null;
        _elapsed = 0f;
        ApplyToCamera((Vector2.Zero, 0f));
    }

    /// <summary>
    /// 推进一帧（公开步进，测试手动调用；ProcessMode 正常时引擎每帧自动调）。返回本帧应用给相机的平移偏移。
    /// Duration&gt;0 时本帧一过总时长就自动停止（末尾减弱由 Sample 的包络完成，不会在最后帧突兀切 0）；
    /// Duration=0 无限循环，只有 Stop() 才结束。未在震时返回 (0,0) 且不动相机。
    /// </summary>
    public Vector2 Step(double delta)
    {
        if (_preset == null) return Vector2.Zero;
        _elapsed += (float)delta;
        var (offset, roll) = Sample(_preset, Seed, _elapsed);
        ApplyToCamera((offset, roll));
        if (_preset.Duration > 0f && _elapsed >= _preset.Duration) Stop();   // 到时自动停止（Envelope 已让本帧振幅 = 0）
        return offset;
    }

    /// <summary>
    /// 振幅包络（剩余振幅比例，[0,1] 附近）：Duration&gt;0 时随已播时长从 1 递减到 0，形状由预设控制——
    /// EnableEase=true 按 Ease 曲线（Envelope = 1 − Evaluate(Ease, 已播/总时长)：Out* 开头快结尾慢 = 先猛后轻；
    /// Back/Bounce/Elastic 会瞬时略过 1/0，属回弹/弹跳的正常过冲）；EnableEase=false 退化为**直线**
    /// （Envelope = 1 − 已播/总时长，无缓动、匀速减到 0）；Duration=0 恒 1（无限循环满幅）。
    /// 纯函数、确定性，可单独单测。
    /// </summary>
    public static float Envelope(ScreenShakePreset preset, float elapsed)
    {
        if (preset.Duration <= 0f) return 1f;   // 无限循环：恒定满幅
        var t = Mathf.Clamp(elapsed / preset.Duration, 0f, 1f);
        return preset.EnableEase ? 1f - ShakeEases.Evaluate(preset.Ease, t) : 1f - t;
    }

    /// <summary>
    /// 纯函数采样：给定预设 + 种子 + 已播放时长（秒），返回（画布平移偏移 px, 镜头滚动度数）。
    /// 无任何实例状态；preset 无效一律返回 (0,0)。位移方向走确定性平滑噪声
    /// （每轴独立随机游走、钳在半径 1 的圆内），同一 (preset, seed, t) 永远返回同一值。
    /// 振幅乘以包络 Envelope（见上）：Duration&gt;0 时到点即 (0,0)、末尾按 Ease 提前减弱。
    /// </summary>
    public static (Vector2 Offset, float Roll) Sample(ScreenShakePreset? preset, uint seed, float elapsed)
    {
        if (preset == null) return (Vector2.Zero, 0f);
        if (preset.Duration > 0f && elapsed >= preset.Duration) return (Vector2.Zero, 0f);   // 已播完：安静
        var env = Envelope(preset, elapsed);

        var phase = elapsed * preset.Frequency;
        var nx = ValueNoise(seed, phase);                                  // x 轴游走
        var ny = ValueNoise(seed ^ 0xF0F0F0F0u, phase * 1.31f + 1000f);    // y 轴游走（不同相位/种子 → 两轴不相关）
        var dir = new Vector2(nx * 2f - 1f, ny * 2f - 1f);                 // 半径 1 圆内一点（钳到圆内）
        if (dir.LengthSquared() > 1f) dir = dir.Normalized();
        var offset = dir * (preset.Amplitude * env);                       // 位移 ≤ Amplitude×env：减弱期缩小到安静
        var roll = preset.RotationDegrees > 0f
            ? (ValueNoise(seed ^ 0x5D5D5D5Du, elapsed * 6f + 4242f) * 2f - 1f) * preset.RotationDegrees * env
            : 0f;
        return (offset, roll);
    }

    /// <summary>把采样结果写进相机（Offset + 基础旋转上的滚动偏移）。相机失效时静默忽略。</summary>
    private void ApplyToCamera((Vector2 Offset, float Roll) s)
    {
        if (_camera == null || !IsInstanceValid(_camera)) return;
        _camera.Offset = s.Offset;
        _camera.RotationDegrees = _restRotation + s.Roll;
    }

    /// <summary>解析目标相机：优先 CameraPath；否则从本节点向上找最近的 Camera2D（把自己放进相机的子节点即命中）。</summary>
    private Camera2D? ResolveCamera()
    {
        if (CameraPath != null) return GetNodeOrNull<Camera2D>(CameraPath);
        for (Node? n = this; n != null; n = n.GetParent())
        {
            if (n is Camera2D cam) return cam;
        }
        return null;
    }

    /// <summary>
    /// 确定性 1D 值噪声：对相位 phase（波长 = 1 格，格间 smoothstep 插值）采样，
    /// 纯整数哈希 + 一次除法，任何平台/线程结果一致。输出 [0,1]。seed 用来区分不同轴/不同预设。
    /// </summary>
    private static float ValueNoise(uint seed, float phase)
    {
        var cell = Mathf.Floor(phase);
        var frac = phase - cell;
        var k = (int)cell;   // phase 在本组件里恒 ≥ 0，k 非负
        var a = Hash01(seed, k);
        var b = Hash01(seed, k + 1);
        var s = frac * frac * (3f - 2f * frac);   // smoothstep
        return a + (b - a) * s;
    }

    private static float Hash01(uint seed, int k)
    {
        unchecked
        {
            uint h = seed * 374761393u + (uint)k * 668265263u + 0x9E3779B9u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777216f;   // [0,1]
        }
    }
}
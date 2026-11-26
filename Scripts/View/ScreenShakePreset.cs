using Godot;

namespace SaleFish.View;

/// <summary>
/// 震屏参数（局外配置，Godot Resource）：一份「震屏特效」的全部数值，可被任意场景/按钮触发。
/// 播放逻辑在 ScreenShaker（组件节点）+ 无状态纯函数 ScreenShaker.Sample —— 预设只是数据，不与播放耦合。
///
/// 两种播放模式（用 Duration 二选一）：
///  ▪ Duration > 0（有限）：总时长，播完后自动停止；末尾按 Ease 曲线「逐渐减弱」收尾（推荐，先满幅 → 按曲线降幅到 0）；
///  ▪ Duration = 0（无限循环）：恒定满幅持续晃动，只有手动 Stop() 才结束（默认值，兼容「点按钮一直震」的调试习惯）。
/// 减弱形状用 Ease 指定（DOTween 同款命名，见 {@link ShakeEase}）：剩余振幅 = 1 − Evaluate(Ease, 已播/总时长)。
/// 代码里还能走 Fluent API：preset.SetEase(Ease.OutBack).SetDuration(0.6f)（对齐 DOTween 的 .SetEase），返回自身可继续链式配置。
///
/// 强度/手感参数：
///  ▪ Amplitude（振幅，世界像素）：实际位移一般不超过此值（Out/in 系缓动恒 ≤ 此值；Back/Bounce/Elastic 收尾有短暂过冲）；
///  ▪ Frequency（频率，Hz）：画面朝随机方向「逛」的速度——高 = 高频细抖，低 = 低频大摆；
///  ▪ RotationDegrees（滚动，度）：叠加的镜头左右滚动量（向随机方向摆），0 = 纯平移。
///
/// 六档内置预设见静态类 ShakePresets（轻微 → 极端），直接 ScreenShaker.Play(ShakePresets.Medium) 即可。
/// 要自定义新震屏：检查器里「快速载入 → 新建 → ScreenShakePreset」建一份 .tres 填数值后复用即可。
/// </summary>
[GlobalClass]
public partial class ScreenShakePreset : Resource
{
    /// <summary>预设名（展示/调试用）。</summary>
    [Export] public string DisplayName = "未命名震屏";

    /// <summary>最大振幅（世界像素）：实际位移一般不超过此值——偏移方向在「半径 = 振幅」的圆内随机游走（圆内钳制）。</summary>
    [Export(PropertyHint.Range, "0,150,1")] public float Amplitude = 12f;

    /// <summary>偏移游走频率（Hz）：画面每秒钟换多少次方向，越高越「抖」、越低越「甩」。</summary>
    [Export(PropertyHint.Range, "1,60,1")] public float Frequency = 20f;

    /// <summary>镜头滚动量（度，左右随机摆）：0 = 纯平移不滚。</summary>
    [Export(PropertyHint.Range, "0,8,0.25")] public float RotationDegrees = 0f;

    /// <summary>
    /// 总时长（秒）：>0 = 播够时长自动停止，末尾按 Ease 曲线逐渐减弱（Envelope = 1 − Ease(已播/总时长)）；
    /// 0 = 无限循环，持续晃动直到手动 Stop()。默认 0.35（中等档），六档内置各有默认时长（见 ShakePresets）。
    /// </summary>
    [Export(PropertyHint.Range, "0,3,0.05")] public float Duration = 0.35f;

    /// <summary>
    /// 是否启用减弱曲线：true = 按时长后半程按 Ease 曲线衰减（Out* 先猛后轻等）；false = 没有缓动，
    /// 收尾退化成**直线衰减**（Envelope = 1 − 已播/总时长，从 1 匀速减到 0）。不影响 Duration——
    /// 两种都到时自动停、都为平滑收尾，只是曲线形状 vs 匀速直线的区别。默认 true。
    /// </summary>
    [Export] public bool EnableEase = true;

    /// <summary>
    /// 结束减弱曲线（枚举，DOTween 风格，默认 OutCubic = 先猛后轻的常用冲击收尾）：
    ///  ▪ Out*（OutQuad/Cubic/Quart/Quint/Expo/Back…）开头快结尾慢 → 先猛后轻、优雅停（冲击手感）；
    ///  ▪ In* 开头慢结尾快 → 前半段挂着、临停前才加剧；
    ///  ▪ InOut* 两头慢中间快；
    ///  ▪ Back/Bounce/Elastic 收尾带回弹/弹跳/弹性起伏（允许短暂过冲振幅）。
    /// 完整曲线清单与公式见 {@link ShakeEase} / {@link ShakeEases}。
    /// </summary>
    [Export] public ShakeEase Ease = ShakeEase.OutCubic;

    /// <summary>链式 API（对齐 DOTween 的 .SetEase）：设置结束减弱曲线，返回本预设继续链式配置。
    /// 例：preset.SetEase(Ease.OutBack).SetDuration(0.6f)  →  回弹收尾的 0.6 秒震屏。
    /// 注：曲线只在 EnableEase=true 时生效；false 走直线衰减。</summary>
    public ScreenShakePreset SetEase(ShakeEase ease)
    {
        Ease = ease;
        return this;
    }

    /// <summary>链式 API：设置总时长（秒；0 = 无限循环直到 Stop），返回本预设继续链式配置。</summary>
    public ScreenShakePreset SetDuration(float seconds)
    {
        Duration = seconds;
        return this;
    }
}
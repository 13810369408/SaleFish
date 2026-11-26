using Godot;

namespace SaleFish.View;

/// <summary>
/// 六档内置震屏预设（轻微 → 极端），随手可得：ScreenShaker.Play(ShakePresets.Strong)。
/// 每档都有**默认时长 + OutCubic 减弱收尾**（静置点击即可看到「越震越轻 → 自然停住」）；想要无限循环，
/// 把某档（或自己的 preset）的 Duration 设 0，就是旧版行为（持续晃动直到 Stop）。
/// 强度只换一组「递增参数」（振幅、滚动逐档加大、频率逐档放低；时长默认也大致逐档加大，
/// 但它是自由手感参数——可随意覆盖、不要求排序，0 = 无限循环），观感从「几乎察觉不到」到「全屏猛晃」。
/// 演示关卡的 6 个按钮直接遍历 All 取用；要第七种手感和风：用 ScreenShakePreset 自定义一份 .tres 即可
/// （代码里还能 preset.SetEase(Ease.OutBack).SetDuration(0.6f) 链式改曲线/时长）。
/// </summary>
public static class ShakePresets
{
    /// <summary>轻微：贴纸/按键配音级，几乎察觉不到的小震动（3px、0.35s、高频细抖、纯平移）。</summary>
    public static readonly ScreenShakePreset Micro = new()
    {
        DisplayName = "轻微", Amplitude = 3f, Frequency = 34f, RotationDegrees = 0f, Duration = 0.35f,
    };

    /// <summary>轻度：小冲击（踩机关/落水），可感知但克制（6px、0.25s、纯平移）。</summary>
    public static readonly ScreenShakePreset Light = new()
    {
        DisplayName = "轻度", Amplitude = 6f, Frequency = 30f, RotationDegrees = 0f, Duration = 0.25f,
    };

    /// <summary>中等：普通命中/碰撞（12px、0.35s），大多数战斗反馈的默认档。</summary>
    public static readonly ScreenShakePreset Medium = new()
    {
        DisplayName = "中等", Amplitude = 12f, Frequency = 26f, RotationDegrees = 0f, Duration = 0.35f,
    };

    /// <summary>较强：重击/落雷，开始带轻微镜头滚动（22px、0.5s、1.5°）。</summary>
    public static readonly ScreenShakePreset Strong = new()
    {
        DisplayName = "较强", Amplitude = 22f, Frequency = 22f, RotationDegrees = 1.5f, Duration = 0.5f,
    };

    /// <summary>剧烈：爆炸/大怪落地，明显的左右甩动 + 滚动（40px、0.7s、2.5°）。</summary>
    public static readonly ScreenShakePreset Violent = new()
    {
        DisplayName = "剧烈", Amplitude = 40f, Frequency = 18f, RotationDegrees = 2.5f, Duration = 0.7f,
    };

    /// <summary>极端：全屏大招/震天动地，不留余力的最大震屏（70px、1s、4°）。</summary>
    public static readonly ScreenShakePreset Extreme = new()
    {
        DisplayName = "极端", Amplitude = 70f, Frequency = 15f, RotationDegrees = 4f, Duration = 1f,
    };

    /// <summary>六档按强度升序：All[0]=轻微 … All[5]=极端（演示关卡的 6 个按钮直接遍历取用）。</summary>
    public static readonly ScreenShakePreset[] All = [Micro, Light, Medium, Strong, Violent, Extreme];
}
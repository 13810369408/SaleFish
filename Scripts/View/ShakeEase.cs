using Godot;

namespace SaleFish.View;

/// <summary>
/// 缓动曲线（名词/公式对齐 Unity DOTween 的 Ease 枚举，方便惯性记忆），用于震屏结束时振幅的「逐渐减弱」。
/// 约定 Evaluate(Ease, P)：把进度 P∈[0,1] 映射到曲线值（[0,1]，Back/Bounce/Elastic 可能有量级 ≤2 的过冲）。
/// ScreenShakePreset.Envelope = 1 − Evaluate(Ease, P)，即 Ease 决定「衰减量」怎么走：
///  ▪ Out*（OutQuad/Cubic/Quart/Quint/Expo/Back…）= 开头快、结尾慢 → 先猛后轻、优雅收尾（冲击手感的默认；
///    OutCubic 即默认档）；OutBack/OutElastic 收尾前带一点回弹；
///  ▪ In*（InQuad/Cubic…）= 开头慢、结尾快 → 前半段挂着、临停前才加剧；
///  ▪ InOut* = 两头慢、中间快；
///  ▪ Bounce/Elastic 系列是一串弹跳/弹性，减弱过程中反复起伏（会短暂过冲、超过设置的振幅，属正常手感）。
/// 要加新曲线：在 ShakeEase 枚举补一个成员、在 ShakeEases.Evaluate 补对应公式，其余全部自动生效。
/// </summary>
public enum ShakeEase
{
    Linear,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InQuint, OutQuint, InOutQuint,
    InSine, OutSine, InOutSine,
    InExpo, OutExpo, InOutExpo,
    InCirc, OutCirc, InOutCirc,
    InBack, OutBack, InOutBack,
    InBounce, OutBounce, InOutBounce,
    InElastic, OutElastic, InOutElastic,
}

/// <summary>缓动求值器：Evaluate(Ease, P) → 曲线值；纯函数、确定性，可单独单测。端点恒有 Evaluate(·,0)=0、Evaluate(·,1)=1。</summary>
public static class ShakeEases
{
    private const float C1 = 1.70158f;   // Back 系列的过冲系数（DOTween 同值）

    public static float Evaluate(ShakeEase ease, float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        switch (ease)
        {
            case ShakeEase.Linear: return t;

            case ShakeEase.InQuad: return t * t;
            case ShakeEase.OutQuad: return t * (2f - t);
            case ShakeEase.InOutQuad:
                return t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;

            case ShakeEase.InCubic: return t * t * t;
            case ShakeEase.OutCubic: return 1f - Mathf.Pow(1f - t, 3f);
            case ShakeEase.InOutCubic:
                return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

            case ShakeEase.InQuart: return Mathf.Pow(t, 4f);
            case ShakeEase.OutQuart: return 1f - Mathf.Pow(1f - t, 4f);
            case ShakeEase.InOutQuart:
                return t < 0.5f ? 8f * Mathf.Pow(t, 4f) : 1f - Mathf.Pow(-2f * t + 2f, 4f) / 2f;

            case ShakeEase.InQuint: return Mathf.Pow(t, 5f);
            case ShakeEase.OutQuint: return 1f - Mathf.Pow(1f - t, 5f);
            case ShakeEase.InOutQuint:
                return t < 0.5f ? 16f * Mathf.Pow(t, 5f) : 1f - Mathf.Pow(-2f * t + 2f, 5f) / 2f;

            case ShakeEase.InSine: return 1f - Mathf.Cos(t * Mathf.Pi / 2f);
            case ShakeEase.OutSine: return Mathf.Sin(t * Mathf.Pi / 2f);
            case ShakeEase.InOutSine: return -(Mathf.Cos(Mathf.Pi * t) - 1f) / 2f;

            case ShakeEase.InExpo: return t <= 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
            case ShakeEase.OutExpo: return t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
            case ShakeEase.InOutExpo:
                return t <= 0f ? 0f : t >= 1f ? 1f :
                    t < 0.5f ? Mathf.Pow(2f, 20f * t - 10f) / 2f : (2f - Mathf.Pow(2f, -20f * t + 10f)) / 2f;

            case ShakeEase.InCirc: return 1f - Mathf.Sqrt(1f - t * t);
            case ShakeEase.OutCirc: return Mathf.Sqrt(1f - (t - 1f) * (t - 1f));
            case ShakeEase.InOutCirc:
                return t < 0.5f ? (1f - Mathf.Sqrt(1f - 4f * t * t)) / 2f
                    : (Mathf.Sqrt(1f - Mathf.Pow(-2f * t + 2f, 2f)) + 1f) / 2f;

            case ShakeEase.InBack:
            {
                var c3 = C1 + 1f;
                return c3 * t * t * t - C1 * t * t;
            }
            case ShakeEase.OutBack:
            {
                var c3 = C1 + 1f;
                return 1f + c3 * Mathf.Pow(t - 1f, 3f) + C1 * Mathf.Pow(t - 1f, 2f);
            }
            case ShakeEase.InOutBack:
            {
                var c2 = C1 * 1.525f;
                return t < 0.5f
                    ? Mathf.Pow(2f * t, 2f) * ((c2 + 1f) * 2f * t - c2) / 2f
                    : (Mathf.Pow(2f * t - 2f, 2f) * ((c2 + 1f) * (2f * t - 2f) + c2) + 2f) / 2f;
            }

            case ShakeEase.InBounce: return 1f - OutBounce(1f - t);
            case ShakeEase.OutBounce: return OutBounce(t);
            case ShakeEase.InOutBounce:
                return t < 0.5f ? (1f - OutBounce(1f - 2f * t)) / 2f : (1f + OutBounce(2f * t - 1f)) / 2f;

            case ShakeEase.InElastic:
            {
                const float c4 = 2f * Mathf.Pi / 3f;
                if (t <= 0f) return 0f;
                if (t >= 1f) return 1f;
                return -Mathf.Pow(2f, 10f * t - 10f) * Mathf.Sin((t * 10f - 10.75f) * c4);
            }
            case ShakeEase.OutElastic:
            {
                const float c4 = 2f * Mathf.Pi / 3f;
                if (t <= 0f) return 0f;
                if (t >= 1f) return 1f;
                return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
            }
            case ShakeEase.InOutElastic:
            {
                const float c5 = 2f * Mathf.Pi / 4.5f;
                if (t <= 0f) return 0f;
                if (t >= 1f) return 1f;
                return t < 0.5f
                    ? -(Mathf.Pow(2f, 20f * t - 10f) * Mathf.Sin((20f * t - 11.125f) * c5)) / 2f
                    : Mathf.Pow(2f, -20f * t + 10f) * Mathf.Sin((20f * t - 11.125f) * c5) / 2f + 1f;
            }

            default: return t;   // 未知成员退化线性（安全兜底）
        }
    }

    /// <summary>OutBounce 的分段弹球公式（InBounce/InOutBounce 复用它）。</summary>
    private static float OutBounce(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) return n1 * (t -= 1.5f / d1) * t + 0.75f;
        if (t < 2.5f / d1) return n1 * (t -= 2.25f / d1) * t + 0.9375f;
        return n1 * (t -= 2.625f / d1) * t + 0.984375f;
    }
}
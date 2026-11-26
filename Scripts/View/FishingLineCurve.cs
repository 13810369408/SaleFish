using Godot;

namespace SaleFish.View;

/// <summary>
/// 鱼线弧线（纯函数、无状态）：把「钓竿 → 鱼钩」的连线在鱼钩下落时改成略微上拱的弧线，
/// 收线/停住时回到笔直。物理直觉：鱼钩被重力拽得快、鱼线中段跟不上 → 中段相对弦线高起 → 向上弯。
///
/// 曲线是「弦 + 剖面」：采样点 = 弦上 lerp(a,b,t) + 世界向上(−Y)的 sag·PeakProfile(t, peak)。
///  ▪ PeakProfile 是两段抛物线拼的平滑帐篷，峰值精确落在 t=peak 处（0.5 = 正中，接近 1 = 靠近鱼钩端）；
///  ▪ sag ≤ 0 → 只返回 [a, b] 两点的笔直线段（"直线"的规范表达，也是零开销的空跑路径）；
///  ▪ sag &gt; 0 → 按长度自适应分段（每约 24px 一段，8..48 段）平滑采样，两端点恒等于 a / b。
/// 目标 sag 由 <see cref="TargetSag"/> 给出（曲度上限与参考速度作为参数从调用方注入，MainStage 检查器可调）。
/// </summary>
public static class FishingLineCurve
{
    /// <summary>目标曲度（纯函数）：收线/停住/未忙或参考速度非正 → 0（笔直）；
    /// 否则按下落速度 vy / sagFallSpeed 从 0 线性爬升到 maxSag。
    /// maxSag / sagFallSpeed 两个曲度参数由调用方（MainStage 的检查器导出属性）注入——曲线规则可测、手感可调。</summary>
    public static float TargetSag(float fallSpeedY, bool taut, float maxSag, float sagFallSpeed)
    {
        if (taut) return 0f;
        if (sagFallSpeed <= 0.001f) return 0f;   // 防除零：参考速度 0 = 永不弯曲
        return Mathf.Clamp(fallSpeedY / sagFallSpeed, 0f, 1f) * maxSag;
    }

    /// <summary>
    /// 拱形剖面（纯函数）：两段抛物线拼成平滑帐篷，峰值（=1）精确落在 t=peak，两端为 0。
    /// 默认二次贝塞尔是它的特例（peak=0.5 → 4t(1−t)，与贝塞尔拱一致）：
    /// peak 越接近 1，拱顶越靠近鱼钩端（下垂的鱼线最松弛点偏下游）。peak 被钳位到 [0.05, 0.95] 防除零。
    /// </summary>
    public static float PeakProfile(float t, float peak)
    {
        var p = Mathf.Clamp(peak, 0.05f, 0.95f);
        if (t <= p)
        {
            var u = t / p;
            return u * (2f - u);
        }
        var v = (1f - t) / (1f - p);
        return v * (2f - v);
    }

    /// <summary>返回「钓竿→鱼钩」线的折线采样（含两端点）：sag ≤ 0 → [a, b] 两点的直线；
    /// sag &gt; 0 → 按 PeakProfile 上拱的弧线（sagPeak 控制拱顶在弦上的位置），每约 24px 一个采样点、8..48 段。</summary>
    public static Vector2[] Samples(Vector2 a, Vector2 b, float sag, float sagPeak)
    {
        if (sag <= 0f) return [a, b];
        var dist = (b - a).Length();
        var segments = Mathf.Clamp((int)(dist / 24f), 8, 48);
        var pts = new Vector2[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var t = i / (float)segments;
            var chord = a.Lerp(b, t);
            pts[i] = chord + new Vector2(0f, -sag * PeakProfile(t, sagPeak));
        }
        return pts;
    }
}
namespace SaleFish.Fishing;

/// <summary>
/// 鱼行为数据（纯 C# 值类型，非 Resource）：一次传给 Fish 的巡逻/追饵参数快照。
/// 来源：内置 ClassicBehavior 每帧把自己的 7 个导出参数打包一份，经 ctx.BehaviorData 写进鱼
/// （自定义策略想用巡逻矩形等几何原语时也可自行构造一份）；Fish 侧的巡逻矩形/深度区间/
/// 感应半径/追逐距离全读这份数据。
/// 值类型 → 策略实例天然无每鱼状态，同一策略被多条鱼共享也安全。
/// </summary>
public struct FishBehaviorData
{
    /// <summary>游动深度区间（下限，相对水面，px；水面往下为正）。</summary>
    public float SwimDepthMin;

    /// <summary>游动深度区间（上限，相对水面，px）。</summary>
    public float SwimDepthMax;

    /// <summary>左右巡逻范围（半宽 px，出生点 x 左右各能往返游动的距离）。&lt;=0 = 未配置 → 取当前水深区间宽度。</summary>
    public float PatrolDistanceX;

    /// <summary>巡逻游动速度（px/s）：平时随机游动的速度。</summary>
    public float PatrolSpeed;

    /// <summary>追饵速度（px/s）：饵进入感应范围后朝饵接近的速度（比巡逻快才追得上）。</summary>
    public float ChaseSpeed;

    /// <summary>感知半径（px）：饵距鱼小于它 → 进入追逐状态。</summary>
    public float SenseRadius;

    /// <summary>追逐距离（px，相对出生点）：追饵途中离出生点超过它 → 放弃、回出生点附近重新巡逻。</summary>
    public float ChaseRange;

    /// <summary>未配置行为的兜底（等同旧「Behavior 为 null」时的内置参数：横向范围 0 = 取水深区间宽度）。</summary>
    public static FishBehaviorData Unconfigured => new()
    {
        SwimDepthMin = 20f, SwimDepthMax = 200f,
        PatrolSpeed = 60f, ChaseSpeed = 120f,
        SenseRadius = 150f, ChaseRange = 800f,
    };
}
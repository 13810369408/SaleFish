using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 内置行为策略（经典款）：数据驱动的巡逻/追饵/返回状态机，从原 Fish 内置逻辑逐行搬移，数值手感不变。
///
/// 行为数据直接成为本策略的导出参数（7 个，见下）——不再有独立的行为数据 Resource：
/// 每种配法的巡逻行为 = 一个带参数的 ClassicBehavior .tres 实例（可被多条鱼共享），
/// 默认值 = 原独立行为数据类的字段默认（PatrolDistanceX=200 / ChaseSpeed=150 等）。
/// 运行时每帧把导出参数打包成 FishBehaviorData 经 ctx.BehaviorData 写进鱼（巡逻矩形/深度读它）。
///
/// 普通鱼想要旧手感：BehaviorStrategy 槽选「ClassicBehavior」即可，默认参数也能游。
/// 想为特殊鱼种写自己的行为：继承 FishBehaviorBase 完全接管（本类可以当范例抄）。
/// </summary>
[GlobalClass]
public partial class ClassicBehavior : FishBehaviorBase
{
    /// <summary>左右巡逻范围（半宽 px）：出生点 x 左右各能往返游动的距离（刷新位置的 x 也在此范围内随机）。
    /// 0 = 未配置 → 取当前水深区间宽度。</summary>
    [Export] public float PatrolDistanceX = 200f;

    /// <summary>巡逻游动速度（px/s）：平时随机游动的速度。</summary>
    [Export] public float PatrolSpeed = 60f;

    /// <summary>追饵速度（px/s）：饵进入感应范围后朝饵接近的速度（比巡逻快才追得上）。</summary>
    [Export] public float ChaseSpeed = 150f;

    /// <summary>感知半径（px）：饵距鱼小于该值 → 进入追逐状态。</summary>
    [Export] public float SenseRadius = 150f;

    /// <summary>追逐距离（px，相对出生点）：追饵途中离出生点超过该值 → 放弃、回出生点附近重新进入巡逻
    /// （回到出生点附近前不理会饵，等价于「回巢再战」，无冷却时间）。</summary>
    [Export] public float ChaseRange = 800f;
    
    /// <summary>把导出参数打包成给鱼的运行时数据快照（Fish 侧巡逻矩形/深度/感应/追逐距离读它）。</summary>
    public FishBehaviorData ToData() => new()
    {
        SwimDepthMin = SwimDepthMin,
        SwimDepthMax = SwimDepthMax,
        PatrolDistanceX = PatrolDistanceX,
        PatrolSpeed = PatrolSpeed,
        ChaseSpeed = ChaseSpeed,
        SenseRadius = SenseRadius,
        ChaseRange = ChaseRange,
    };

    public override void Update(FishContext ctx, double delta)
    {
        ctx.BehaviorData = ToData();   // 数据进鱼：巡逻矩形/深度/感应/追逐距离本帧即用（也可预填，见生成器）

        var dt = (float)delta;
        switch (ctx.CurrentState)
        {
            case Fish.State.Patrol: StepPatrol(ctx, dt); break;
            case Fish.State.Chase: StepChase(ctx, dt); break;
            case Fish.State.Returning: StepReturning(ctx, dt); break;
        }
    }

    private static void StepPatrol(FishContext ctx, float dt)
    {
        var bait = ctx.BaitPosition;
        if (bait.HasValue && bait.Value.DistanceTo(ctx.Position) < ctx.SenseRange())
        {
            ctx.SetState(Fish.State.Chase);
            return;
        }
        if (ctx.Position.DistanceTo(ctx.CurrentTarget) < 8f) ctx.PickNextTarget();   // 游到目标 → 换一个点
        ctx.MoveToward(ctx.CurrentTarget, ctx.BehaviorData.PatrolSpeed, dt, patrolClamp: true);
    }

    private static void StepChase(FishContext ctx, float dt)
    {
        var bait = ctx.BaitPosition;
        if (!bait.HasValue) { ctx.SetState(Fish.State.Returning); return; }                 // 饵没了（收线/离水）→ 回去
        var dist = bait.Value.DistanceTo(ctx.MouthPosition);                              // 以鱼嘴为锚点判贴脸
        if (dist < ctx.BiteRange) { ctx.EnterBitten(); return; }                         // 鱼嘴贴脸 → 咬中
        if (ctx.Position.DistanceTo(ctx.SpawnPoint) > ctx.ChaseRange()) { ctx.SetState(Fish.State.Returning); return; }   // 追过头 → 放弃
        ctx.MoveMouthToward(bait.Value, ctx.BehaviorData.ChaseSpeed, dt, patrolClamp: false);   // 追饵：让鱼嘴（不是中心）贴上饵，不夹巡逻范围
    }

    private static void StepReturning(FishContext ctx, float dt)
    {
        if (ctx.Position.DistanceTo(ctx.SpawnPoint) <= ctx.ReturnHomeDistance)
        {
            ctx.SetState(Fish.State.Patrol);
            ctx.PickNextTarget();
            return;
        }
        ctx.MoveToward(ctx.SpawnPoint, ctx.BehaviorData.PatrolSpeed, dt, patrolClamp: false);
    }
}
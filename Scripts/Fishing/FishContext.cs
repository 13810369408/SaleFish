using Godot;

namespace SaleFish.Fishing;

/// <summary>鱼的动画播放状态（Fish 的 AnimatedSprite2D 用哪档 SpeedScale）。</summary>
public enum FishPlayback
{
    /// <summary>平时自由游动 / 上钩但安静待收线（恒为默认档）。</summary>
    Normal,
    /// <summary>上钩且正在挣扎。</summary>
    Struggle,
    /// <summary>上钩且体力耗尽（挣扎不动了）。</summary>
    Exhausted,
}

/// <summary>
/// 鱼与钓的统一决策上下文（每帧由 Fish.Step / Hook.StepHooked 构造，作为 FishBehaviorBase.Update、
/// FishStruggleBase.Update / OnHooked 的实参）：把「这一帧策略能读到什么、能下达哪些操作」以纯 C#
/// 接口暴露给策略——它只传递读/写，不做任何逻辑判断。
///
/// 行为与挣扎两个阶段共用一个类，按「谁构造」决定有效成员（另一侧的成员读默认值、写无效果）：
///  ▪ 行为阶段（Fish.Step 构造，传鱼）：CurrentState / Position / SpawnPoint / BehaviorData / Def /
///    BaitPosition / BiteRange / MouthPosition / ReturnHomeDistance / CurrentTarget，以及水体几何 Water / Bounds /
///    DistanceToSurface / DistanceToBottom / DistanceToLeft / DistanceToRight / IsInWater 可读；
///    SetState / MoveToward / PickNextTarget / RandomPointIn / PatrolRangeX / SenseRange / ChaseRange /
///    EnterBitten 可调；挣扎侧成员（ReelHeld / HookPosition / CastOrigin / LineLength …）读默认值。
///  ▪ 挣扎阶段（Hook.StepHooked 构造，传钩）：ReelHeld / HookPosition / CastOrigin / LineLength /
///    MaxTension / Fish / Struggle / Struggling / PullVelocity / ReleaseRequested / PullDirection /
///    PickDefaultPullDirection / StruggleWasStruggling 有效；行为侧成员直接读上钩那条鱼
///    （CurrentState = Bitten、Position = 鱼位置等），水体几何经钩子的水体读。
///
/// 规则边界（Hook 负责的「机械层」，不交给鱼）：线长钳制 / 拉到线长极限 → 自动脱钩；收线回到
/// 抛出起点 → 钓上（Caught）；脱钩后鱼消失、装备清空、鱼线自动收回、一竿结束。挣扎实现只通过
/// 三个输出口表达意图：PullVelocity —— 本帧把钩子拖走的运动速度（px/s，世界方向；零 = 不拖，
/// 此时玩家按住左键即为正常收线回拉）；ReleaseRequested —— 主动脱钩请求（效果等同拉力拉断）；
/// PlaybackState —— 本帧鱼的动画播放状态（Normal/Struggle/Exhausted，Hook 应用给鱼的 SpeedScale）。
///
/// 两个阶段共用同一套底层：行为侧移动原语走 Fish 内部（转向翻转动画、巡逻矩形钳制），挣扎侧输出
/// 走 Hook.StepHooked——保证被任一策略接管的鱼/挣扎与内置策略共用同一套「走路/挣扎」规则。
/// </summary>
public sealed class FishContext
{
    private readonly Fish? _fish;
    private readonly Hook? _hook;
    private FishStruggle? _fallbackStruggle;   // 行为阶段误读挣扎机时的无害空机（延迟分配，避免每帧 new）

    /// <summary>行为阶段构造：由 Fish.Step 每帧传入本鱼（挣扎侧成员此时读默认值）。</summary>
    internal FishContext(Fish fish)
    {
        _fish = fish;
        _hook = null;
    }

    /// <summary>挣扎阶段构造：由 Hook.BeginHookUp / StepHooked 传入本钩（鱼 = hook.HookedFish）。</summary>
    internal FishContext(Hook hook)
    {
        _hook = hook;
        _fish = hook.HookedFish;
    }

    /// <summary>这条鱼（行为阶段 = 传入的鱼；挣扎阶段 = 上钩的鱼 Hook.HookedFish）。</summary>
    public Fish? Fish => _fish;

    // ---- 只读：本帧可读的鱼状态（行为阶段决策输入；挣扎阶段经 HookedFish 读同一份） ----

    /// <summary>当前状态（行为：Patrol / Chase / Returning / Bitten；挣扎阶段的鱼冻结在 Bitten）。</summary>
    public Fish.State CurrentState => _fish?.Current ?? default;

    /// <summary>鱼当前位置（世界坐标）。</summary>
    public Vector2 Position => _fish?.GlobalPosition ?? Vector2.Zero;

    /// <summary>鱼嘴世界坐标（咬钩判定锚点：鱼嘴距饵 &lt; BiteRange 才咬中，而非鱼身中心）。</summary>
    public Vector2 MouthPosition => _fish?.MouthGlobalPosition ?? Vector2.Zero;

    /// <summary>鱼出生点（巡逻范围 / 追逐距离 / 返回目标的锚）：_Ready 记录部署位置。</summary>
    public Vector2 SpawnPoint => _fish?.SpawnPoint ?? Vector2.Zero;

    /// <summary>行为数据（本帧生效的巡逻/追饵参数快照，值类型；未配置 = Unconfigured 兜底）。
    /// 内置 ClassicBehavior 每帧把自己的 7 个导出参数打包写进来（数据 → 数据流）；
    /// 自定义策略不读这份数据时可以完全无视它。</summary>
    public FishBehaviorData BehaviorData
    {
        get => _fish?.BehaviorData ?? default;
        set { if (_fish != null) _fish.BehaviorData = value; }
    }

    /// <summary>本鱼/上钩鱼的鱼种定义（策略想读 FishDef 上的公共输入——体力/耐力/拖速等——从这里拿）。</summary>
    public FishDef? Def => _fish?.Def;

    /// <summary>饵位置（已做「在水里」过滤：钩子收起/离水 → null，鱼不感应）。
    /// 无水域时认为饵在水里。</summary>
    public Vector2? BaitPosition => _fish?.BaitPosition();

    /// <summary>咬中判定距离：追饵贴脸（距饵 &lt; BiteRange）→ 判定咬中。</summary>
    public float BiteRange => _fish?.BiteRange ?? 0f;

    /// <summary>回到出生点附近（距离 px）即视为返回完成 → 重新进巡逻。</summary>
    public float ReturnHomeDistance => _fish?.ReturnHomeDistance ?? 0f;

    /// <summary>当前巡逻目标点（PickNextTarget 之后更新；游到它附近换下一个）。</summary>
    public Vector2 CurrentTarget => _fish?.CurrentTargetInternal ?? Vector2.Zero;

    // ---- 只读：水几何（行为阶段读鱼的、挣扎阶段读钩子的；都来自同一个 WaterSpace） ----
    // 距离都不扣身体半径，负数 = 已经越过那条边界（在水外 / 水面上方 / 水底下方）。
    // 策略要留余量就自行比较：「DistanceToSurface < 100 → 不许再向上」。没有水域时距离为 +∞。

    /// <summary>所在水体的几何（水面 / 左右岸 / 水底折线）；没有可用水域 → null。</summary>
    public WaterSpace? Water => _fish?.ResolveSpace() ?? _hook?.ResolveSpace();

    /// <summary>到四面（左岸/右岸/水面/水底）的距离快照；无水域 → null。</summary>
    public WaterBounds? Bounds => Water?.BoundsAt(Position);

    /// <summary>到水面的距离（px，向下为正）。</summary>
    public float DistanceToSurface => Water?.BoundsAt(Position).ToSurface ?? float.PositiveInfinity;

    /// <summary>到当前 x 处水底的距离（px，向下为正）。</summary>
    public float DistanceToBottom => Water?.BoundsAt(Position).ToBottom ?? float.PositiveInfinity;

    /// <summary>到左岸的距离（px，向右为正）。</summary>
    public float DistanceToLeft => Water?.BoundsAt(Position).ToLeft ?? float.PositiveInfinity;

    /// <summary>到右岸的距离（px，向左为正）。</summary>
    public float DistanceToRight => Water?.BoundsAt(Position).ToRight ?? float.PositiveInfinity;

    /// <summary>当前位置是否在水里（水面上方 / 水底下方 / 岸外都算不在水里）。</summary>
    public bool IsInWater => Water?.IsWater(Position) ?? false;

    // ---- 只读：玩家输入 / 几何（挣扎阶段决策输入；行为阶段读默认值） ----

    /// <summary>玩家是否按住左键（挣扎中施力、安静时收线回拉）。</summary>
    public bool ReelHeld => _hook?.ReelHeld ?? false;

    /// <summary>钩子当前位置（世界坐标）。</summary>
    public Vector2 HookPosition => _hook?.GlobalPosition ?? Vector2.Zero;

    /// <summary>抛出起点（鱼线锚点）：_Ready 记录的场景部署位置，每竿从它出发。</summary>
    public Vector2 CastOrigin => _hook?.CastOrigin ?? Vector2.Zero;

    /// <summary>当前线长（px，钩子到抛出起点的距离）。</summary>
    public float LineLength => _hook != null ? (HookPosition - CastOrigin).Length() : 0f;

    /// <summary>鱼线最大拉力（默认实现：拉力顶到它 → 脱钩）。</summary>
    public float MaxTension => _hook?.Line?.MaxTension ?? 0f;

    /// <summary>当前收线水面间隙 X（px，按住左键收线时钩子保持的最低距水面距离）：
    /// 读取 = 当前生效值；写入 = 立即生效。上钩的鱼可在 OnHooked / Update 里读改
    /// （FishDef.HookupSurfaceGap 的数据驱动写入发生在 OnHooked 之前，这里可再覆盖）；
    /// 脱钩或钓上后 Hook 自动重设回自己的初始值（ReelSurfaceGap）。行为阶段读为 0。</summary>
    public float ReelSurfaceGap
    {
        get => _hook?.CurrentReelSurfaceGap ?? 0f;
        set { if (_hook != null) _hook.CurrentReelSurfaceGap = value; }
    }

    // ---- 挣扎数值机（默认实现驱动；完全接管实现可读写它或无视） ----

    /// <summary>体力/耐力/拉力数值机：体力耐力消耗恢复、拉力升落、停止/再挣扎判定全在里面
    /// （压力/耐力被完全接管的实现绕过时，由该实现自己决定要不要碰它）。
    /// 只应在挣扎阶段读；行为阶段读它 = 延迟分配的无害空机，不会崩但没有意义。</summary>
    public FishStruggle Struggle => _hook?.Struggle ?? (_fallbackStruggle ??= new FishStruggle());

    /// <summary>是否正在挣扎（镜像 Struggle.Struggling；完全接管实现改它可让「挣扎/安静」/
    /// 调试标签语义一致）。</summary>
    public bool Struggling
    {
        get => Struggle.Struggling;
        set => Struggle.Struggling = value;
    }

    // ---- 输出（Hook 每帧执行，见 Hook.StepHooked） ----

    /// <summary>本帧把钩子拖走的运动速度（px/s，世界方向）。零 = 不拖（玩家按住左键 → 收线回拉）。
    /// 默认实现：挣扎中 = 拖行方向 × 拖速（按住左键施力减半），安静 = 零。</summary>
    public Vector2 PullVelocity;

    /// <summary>请求脱钩（效果等同拉力拉断：鱼消失、装备全清、鱼线自动收回）。
    /// 默认实现：拉力顶到鱼线最大承载力（Struggle.Released）。</summary>
    public bool ReleaseRequested;

    /// <summary>本帧鱼的动画播放状态（Normal/Struggle/Exhausted）：挣扎策略写它，Hook 每帧应用给
    /// 鱼的 AnimatedSprite2D.SpeedScale（三档倍率读 FishDef）。默认 Normal；平时自由游动不需要写。</summary>
    public FishPlayback PlaybackState;

    // ---- 挣扎默认方向机：默认实现用它；完全接管可复用或改 ----

    /// <summary>本轮挣扎的拖行方向（默认 = 远离抛出起点 ±60° 摆动，每轮挣扎重选一次）。</summary>
    public Vector2 PullDirection
    {
        get => _hook?.PullDirectionInternal ?? Vector2.Zero;
        set { if (_hook != null) _hook.PullDirectionInternal = value; }
    }

    /// <summary>按默认规则重选拖行方向（远离抛出起点，随机 ±60° 摆动）。</summary>
    public void PickDefaultPullDirection() => _hook?.PickPullDirection();

    /// <summary>上一帧是否在挣扎（跨帧保持）：默认实现用它检测「挣扎轮切换」→ 重选拖行方向。
    /// 完全接管实现不需要方向轮切语义时可不理会。</summary>
    public bool StruggleWasStruggling
    {
        get => _hook?.WasStrugglingInternal ?? false;
        set { if (_hook != null) _hook.WasStrugglingInternal = value; }
    }

    // ---- 挣扎反馈（一行触发：震屏 + 特效；数据全部来自 FishDef 「挣扎效果」分类的导出字段） ----

    /// <summary>
    /// 播放本鱼鱼种配置的挣扎反馈（一行调用）：FishDef.StruggleScreenShake（震屏）+
    /// FishDef.StruggleFxPrefab（特效）。任一未配置即跳过，两者可独立使用。
    /// 只能在挣扎阶段调用（OnHooked / Update 里，此时 Def 是上钩的鱼）：行为阶段 Def 为 null/无意义。
    ///
    /// 实现转调公共播放器 StruggleFeedback.Play（无钩子场合 — 例如演示关 — 也可用同一个类，
    /// 锚点与位置随调用方给；这里是钩子位置 + 钩子取场景树根）。
    /// </summary>
    public void PlayStruggleFeedback()
    {
        var hook = _hook;
        if (hook == null || !GodotObject.IsInstanceValid(hook)) return;   // 没钩子（行为阶段误调）就没人可播
        StruggleFeedback.Play(Def, hook, hook.GlobalPosition);
    }

    // ---- 行为操作：每帧决策后执行（行为阶段有效；挣扎阶段无效果） ----

    /// <summary>切换状态（Patrol / Chase / Returning / Bitten）。</summary>
    public void SetState(Fish.State state) => _fish?.ChangeState(state);

    /// <summary>朝目标以给定速度移动一帧（自动做转向翻转动画；patrolClamp = true 时严格钳在
    /// 巡逻矩形内，追饵/返回放开）。速度单位 px/s。</summary>
    public void MoveToward(Vector2 target, float speed, float dt, bool patrolClamp) =>
        _fish?.MoveToward(target, speed, dt, patrolClamp);

    /// <summary>朝目标以给定速度移动一帧，但以<b>鱼嘴</b>为锚：先按「哪个朝向嘴离目标更近」定朝向，
    /// 再把中心目标修正为 target − 嘴偏移，步长走到时鱼嘴正好落在目标上。追饵/追钩用这个
    /// （普通巡逻仍用 MoveToward 走鱼身中心）。</summary>
    public void MoveMouthToward(Vector2 target, float speed, float dt, bool patrolClamp) =>
        _fish?.MoveMouthToward(target, speed, dt, patrolClamp);

    /// <summary>在巡逻矩形内随机选一个新目标点（进入巡逻 / 游到目标后调用）。</summary>
    public void PickNextTarget() => _fish?.PickNextTarget();

    /// <summary>巡逻矩形内随机一点（y 落在水面下深度区间、x 在出生点 ± 巡逻范围）。</summary>
    public Vector2 RandomPointIn() => _fish?.RandomPointIn() ?? Vector2.Zero;

    /// <summary>左右巡逻范围（px，出生点 X ± 此值）；行为缺省时退回「当前水深区间宽度」。</summary>
    public float PatrolRangeX() => _fish?.PatrolRangeX() ?? 0f;

    /// <summary>感知半径（px）：饵距鱼小于它 → 进入追逐。</summary>
    public float SenseRange() => _fish?.SenseRange() ?? 0f;

    /// <summary>追逐距离（px，相对出生点）：追饵途中离出生点超过它 → 放弃返回。</summary>
    public float ChaseRange() => _fish?.ChaseRange() ?? 0f;

    /// <summary>判定咬中：进入 Bitten 并触发 Fish.Bitten 事件（追饵贴脸的判定由实现负责，
    /// 内置实现是「距饵 &lt; BiteRange」）。</summary>
    public void EnterBitten()
    {
        if (_fish == null) return;
        SetState(Fish.State.Bitten);
        _fish.NotifyBitten();
    }
}
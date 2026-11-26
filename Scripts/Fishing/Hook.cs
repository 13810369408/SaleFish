using System;
using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼钩（Area2D 触发器）：抛竿（Cast）→ 空中抛物线飞行（真实运动学）→ 接触 Water 区域后缓慢下沉 → 停住；
/// 落水后点击左键进入收线模式（Reeling）：钩子受重力 + 鱼线约束沿圆弧荡向起点正下方，
/// 按住左键以「收线速度」朝起点直线收回；收线收完（距起点 < ReelFinishDistance）→ 一竿结束。
///
/// 抛射不再是「蓄力→距离」的参数化曲线，而是**真实运动学**（每帧更新 vx/vy 并积分位移）：
///  ▪ 水平速度：power01 0→1 线性映射 MinSpeed → MaxSpeed（逆时针右向抛射）；
///  ▪ 空中重力：vy 每帧 += AirGravity × GravityBase × δt（重力系数默认 1）− 空气阻力 AirResist×vy×δt（g 恒定，重量只减小阻力项）；
///  ▪ 入水后：两环境共用「900 重力 + 阻力」的运动模型（唯一区别是阻力：空气 AirResist / 水中 WaterResist）——
///    水中阻力大 → 入水迅速刹到水中终端速度（≈900×√Weight/WaterResist）后匀速下潜（不做单摆，直线竖直）；
///    水平漂移同用水中阻力快速停稳；
///  ▪ 没碰到水（抛过头/岸上）→ 飞行 MaxAirTime 秒后兜底落地。
///
/// 收线模式（Reeling，详见 BeginReel / StepReeling）：
///  ▪ 落水后（Sinking）点击左键进入；线长锁定 = 点击瞬间「钩子到抛出起点」的距离；以后按「当前线长」画弧；
///  ▪ 不按左键时：钩子受重力 + 线长约束（单摆模型，真实摆动公式——切向重力 g·sinθ + 阻尼，底端无切向加速度，
///    不会出现「勾股底端越摆越快」的奇点），沿圆弧荡向起点正下方；水中阻尼大 → 过阻尼，落到最低点就停、不摆回；
///    途中先碰到水底 → 停在水底；荡到起点正下方 → 停住；两种停住都不结束这一竿，随时可再收线；
///  ▪ 按住左键时：收线按「收线水面间隙 X（ReelSurfaceGap）+ 最终拉升区（LiftUnlockRange）」推进：
///    进入拉升区（|钩子X−锚点X| ≤ LiftUnlockRange）→ **无论多深都受重力**的单摆收线（带内/更深 = 水中重力、
///    太浅/水上 = 空气重力），横向走单摆公式、线每帧同时缩短——取代旧直线拉升，不会无视重力直线穿水面把钩提起；
///    太浅/水上（深度 &lt; X×0.9）→ 圆弧收线，**一律按空气重力下摆**（需求 5：不区分水下/水上）勾股钉回弧上，直到回带；
///    深度骑到 X（±10% 容差带）→ 保持高度、水平移向锚点；深处（深度 &gt; X）→ 直线朝锚点拉回（不受重力）；
///    线长随收线缩短；松开左键立刻恢复重力，沿收紧后的新线长圆弧摆回（弧线碰到水底就重新落回水底）；
///  ▪ 收线收到距起点 < ReelFinishDistance → 一竿结束（Rest），抛竿按钮恢复；上钩的鱼可在上钩时修改 X
///    （FishDef.HookupSurfaceGap 或 FishContext.ReelSurfaceGap），脱钩 / 钓上后 X 重设回初始值。
///
/// 节点结构：Hook(Area2D) > CollisionShape2D（小圆，接触判定）+ 视觉子节点
/// （金色菱形 Polygon2D 占位，之后换成你的 Sprite2D/贴图即可，逻辑完全不变）。
///
/// 水判定：Water 挂 WaterBody → 用它烘焙好的水体几何（水面 / 左右岸 / 湖床折线，下沉会停在水底折线上）；
/// 普通 Area2D 的矩形碰撞体**未旋转**时用全局包围盒做点判定（确定性、测试友好，完全等价旧的平水底）；
/// 若以后水区域旋转/异形，回退 Area2D.OverlapsArea 物理查询。被挣扎拖走时位移过一遍水体边界（撞边沿边滑）。
/// 运动自包含：_PhysicsProcess 每物理帧推进；自动化测试用公开 Step(delta) 手动注入
/// （把本节点 ProcessMode 设 Disabled 可停掉真实物理循环，步进完全由测试驱动，见 TestFishing）。
/// </summary>
public sealed partial class Hook : Area2D
{
    // ---- 可调参数（编辑器检查器直接可见/可改） ----

    /// <summary>钩子重量倍率（1 = 标准钩；&gt;1 更重，&lt;1 更轻）。重量<b>不改变重力加速度 g</b>（g 与质量无关），
    /// 而是作为「阻力抗性」：更重 → 空气/水中阻力对它的减速影响更小 → 空中下落更快、水中下沉更快。</summary>
    [Export] public float Weight { get; set; } = 1f;

    /// <summary>空气阻力系数（1/s）：空中每帧 vy −= AirResist×vy×δt / √Weight。
    /// 阻力与速度方向相反（自动），是"重物下落更快"的物理来源：g 恒定，质量只减小阻力项。</summary>
    [Export] public float AirResist { get; set; } = 0.5f;

    /// <summary>抛出水平速度下限（power=0 → MinSpeed，px/s）。</summary>
    [Export] public float MinSpeed { get; set; } = 120f;

    /// <summary>抛出水平速度上限（power=100 → MaxSpeed，px/s）。</summary>
    [Export] public float MaxSpeed { get; set; } = 1200f;

    /// <summary>重力系数（1 = 基准重力 900；空中 / 水中 / 收线摆动共用同一个 g；更大 → 一切下落与摆动更快）。</summary>
    [Export] public float AirGravity { get; set; } = 1f;

    /// <summary>水中阻力系数（1/s，Weight=1 时）：与空气阻力同构——两种环境都是「900 重力 + 阻力」，
    /// 唯一区别是阻力大小（空气用 AirResist、水中用本值）。重力与重量无关、质量只缩放阻力项：
    /// 水中终端速度 = AirGravity×GravityBase×√Weight / WaterResist（W=1、默认 8 → ≈112.5 px/s，远慢于空气 ≈1800）。
    /// 同一个阻力同时负责三处：入水刹车与匀速下潜（自然沉）、水中摆动阻尼（默认 8 → 过阻尼，
    /// 荡到最低点就停、不摆回另一边）、水中水平漂移衰减。想调手感就改这一个变量。</summary>
    [Export] public float WaterResist { get; set; } = 8f;

    /// <summary>收线速度（px/s）：收线模式按住左键时，钩子朝抛出起点直线移动的速率。越大收得越快。</summary>
    [Export] public float ReelSpeed { get; set; } = 200f;

    /// <summary>收线水面间隙 X（px，初始值）：收线时钩子保持的「最低距水面距离」——
    /// 深度超过 X 收线照旧直线朝锚点；深度进入 [X×0.9, X×1.1] 容差带时保持高度、水平移向锚点；
    /// 深度不足 X 或在水面上方（太浅）则收线同时按**空气重力**向下摆（需求 5：太浅一律空中重力，
    /// 不再分水下/水上），勾股钉回圆弧，直到回到容差带。上钩的鱼可在上钩后读改当前值（FishContext.ReelSurfaceGap），
    /// 脱钩 / 钓上后重设回这个初始值。</summary>
    [Export] public float ReelSurfaceGap { get; set; } = 10f;

    /// <summary>收线水面间隙的容差带（±10%）：深度 ∈ [X×0.9, X×1.1] 都视为「已骑到 X」。</summary>
    private const float SurfaceGapTolerance = 0.1f;

    /// <summary>最终拉升解锁范围（px）：钩子横向进入「锚点正下方 ±该值」区间时，收线改用**单摆 + 重力**推进
    /// （需求 6：无论钩子多深都受重力——带内/更深 = 水中重力、太浅/水上 = 空气重力），横向走摆动公式、线每帧缩短，
    /// 取代旧的无视重力直线拉升。被拖出该范围 → 回到三档收线规则。</summary>
    [Export] public float LiftUnlockRange { get; set; } = 50f;

    // ---- 装配引用（局外配置的 Resource 数据；运动逻辑不读它们，判定/渲染用） ----

    /// <summary>当前装配的鱼钩定义（贴图 + 默认重量）。null = 未上钩（无配重时用 Weight 字段本身）。</summary>
    [Export] public FishingHookDef? HookDef { get; set; }

    /// <summary>当前装配的真饵（普通鱼饵）。与 LiveBait 二选一；null = 未上真饵。</summary>
    [Export] public BaitDef? Bait { get; set; }

    /// <summary>当前装配的饵鱼（鱼当鱼饵用）。与 Bait 二选一；null = 未上活饵。</summary>
    [Export] public FishDef? LiveBait { get; set; }

    /// <summary>当前装配的配重。null = 无配重（重量 = 钩子默认重量）；有配重时总重量 = 钩子默认重量 + 配重重量（叠加）。</summary>
    [Export] public SinkerDef? Sinker { get; set; }

    /// <summary>当前装配的鱼线（最大拉力 + 线长）。null = 未挂鱼线：不会被咬钩（BeginHookUp 拒绝）、抛出/下沉不限距。</summary>
    [Export] public FishingLineDef? Line { get; set; }

    /// <summary>脱钩（拉力拉断鱼线/被拉到线长极限）后鱼线缓慢自动收回的水平速度（px/s）：自动收线遵循
    /// 与按住左键收线相同的规则（深处直线 / 带内骑线 / 太浅一律空气重力圆弧摆 / 拉升区单摆+重力），
    /// 绳长一样每帧缩短，只有竖直收进速度用本值；收回抛出起点 → 一竿结束。</summary>
    [Export] public float AutoRetractSpeed { get; set; } = 200f;

    // ---- 基准物理常数 ----

    /// <summary>1 倍重力加速度基准（px/s²）。</summary>
    public const float GravityBase = 900f;

    /// <summary>上抛初速度按「基准重力下的峰高」反推：vin 越大弧线越高；AirGravity 会实际压低/抬高峰。</summary>
    public const float AirPeakHeight = 320f;

    /// <summary>空中飞行时长上限（秒）：一直没碰到水（抛过头/岸上）→ 兜底落地停住。</summary>
    public const float MaxAirTime = 4f;

    /// <summary>下沉时长上限（秒）：极端场景（水太深漂太久）兜底停住。</summary>
    public const float MaxSinkTime = 120f;

    /// <summary>收线完成判定：收线时距抛出起点小于该距离（px）→ 一竿结束（Rest）。</summary>
    public const float ReelFinishDistance = 20f;

    /// <summary>荡到「起点正下方」的判定阈值（水平偏离 px，钩子在正下方 ±16px 内视为已荡到位）。</summary>
    private const float ReelVerticalThreshold = 16f;

    public enum State { Idle, Flying, Sinking, Reeling, Hooked, Rest }

    /// <summary>当前状态。</summary>
    public State Current { get; private set; } = State.Idle;

    /// <summary>飞行、入水下沉、收线或上钩挣扎中（此时不可再次抛竿）。收线停住（沉底/正下方待收）也视为忙——一竿未结束。</summary>
    public bool IsBusy => Current is State.Flying or State.Sinking or State.Reeling or State.Hooked;

    /// <summary>当前速度（px/s，测试/调试可见；水中水平分量会衰减到 0）。</summary>
    public Vector2 Velocity => new(_vx, _vy);

    /// <summary>抛竿起点 = 场景摆放位置（钓竿所在）的只读视图：鱼线视觉的一端锚点（另一端 = 钩子当前位置）。
    /// 只读开放给渲染层，物理内部对此字段仍私有。</summary>
    public Vector2 CastOrigin => _home;

    /// <summary>收线模式里左键是否正被按住（由 MainStage 每帧从输入写入；测试直接赋值）。</summary>
    public bool ReelHeld { get; set; }

    /// <summary>当前生效的收线水面间隙 X（px）：初始 = <see cref="ReelSurfaceGap"/>；
    /// 上钩的鱼可通过 FishContext.ReelSurfaceGap 读改；脱钩 / 钓上后重设回初始值。</summary>
    public float CurrentReelSurfaceGap { get; set; }

    /// <summary>收线模式是否已停住（荡到起点正下方或沉到水底、等待继续收线）。停住 ≠ 一竿结束。</summary>
    public bool Parked => _parked;

    /// <summary>一次抛竿真正结束（落岸兜底、收线收完回起点、脱钩/钓上后收线回到起点）时触发：抛竿按钮恢复可用、镜头停止跟随。</summary>
    public event Action? Finished;

    /// <summary>钓上鱼（收线把鱼安全收回抛出起点）时触发：携带被钓上的鱼，由调用方读 fish.Def.ItemDef 生成放置物（数据层在主关卡 MainStage 接）。</summary>
    public event Action<Fish>? Caught;

    /// <summary>上钩挣扎数值机（体力/耐力/拉力，纯 C# 逻辑，见 FishStruggle）——只读视图：主关卡用它驱动测试文字显示。</summary>
    public FishStruggle Struggle { get; } = new();

    /// <summary>当前挂在本钩上的鱼（Hooked 期间持有并带动；脱钩/钓上后为 null）。</summary>
    public Fish? HookedFish => _hookedFish;

    /// <summary>当前是否处于「挣扎拖走」（没按住左键时鱼正把钩拖远）；未上钩/脱钩收线中为 false。</summary>
    public bool Struggling => Current == State.Hooked && !_retracting && Struggle.Struggling;

    /// <summary>当前已拉出的线长 = 钩子到抛出起点的距离（px）。</summary>
    public float LineOut => (GlobalPosition - _home).Length();

    private Area2D? _water;
    private Vector2 _home;    // 场景里摆好的抛出起点（世界坐标）
    private float _vx;        // 当前水平速度（px/s，向右为正）
    private float _vy;        // 当前垂直速度（px/s，向下为正；上抛初速为负）
    private float _time;      // 当前阶段（飞行/下沉/收线摆动/上钩）已用时间（秒）
    private float _radius;    // 收线模式当前线长 = 钩子到抛出起点的半径（收线时缩短）
    private bool _parked;     // 收线模式已停住（等待下次按住左键收线）
    private WaterSpace? _space;   // 水几何缓存（Water 挂 WaterBody 时含湖床折线；普通矩形则退化为平水底）
    private bool _spaceResolved;

    // ---- 上钩状态（BeginHookUp 装配） ----
    private Fish? _hookedFish;    // 正挂在本钩上的鱼（Hooked 期间由本钩持有并带动）
    private Vector2 _pullDir;     // 本轮挣扎拖行方向（远离抛出起点 + 随机 ±60° 摆动）
    private bool _retracting;     // 脱钩后鱼线自动收回起点中（收完 → FinishRest）
    private readonly RandomNumberGenerator _rng = new();   // 挣扎摆动随机方向

    // 挣扎轮切换状态（供内置 ClassicStruggle / 自定义策略经过 FishContext 读写；不需要轮切语义的实现可不理会）
    private bool _wasStruggling;  // 上一帧是否在挣扎（检测挣扎轮切换 → 重选拖行方向）

    /// <summary>内部：本轮挣扎拖行方向（FishContext.PullDirection 的背面）。</summary>
    internal Vector2 PullDirectionInternal
    {
        get => _pullDir;
        set => _pullDir = value;
    }

    /// <summary>内部：上一帧是否在挣扎（跨帧保持，默认挣扎实现经上下文检测轮切换）。</summary>
    internal bool WasStrugglingInternal
    {
        get => _wasStruggling;
        set => _wasStruggling = value;
    }

    /// <summary>让自动化测试用固定随机序列（默认不调用 = 引擎自动随机，真机无需可重现）。</summary>
    public void SeedRandom(ulong seed) => _rng.Seed = seed;

    /// <summary>重量倍率的作用曲线：√Weight（水中终端速度 ∝ √m、阻力影响 ∝ 1/√(质量抗性)）——递减收益，重量的手感不会线性失控。</summary>
    private float WeightFactor => Mathf.Sqrt(Weight);

    public override void _Ready()
    {
        _home = GlobalPosition;
        CurrentReelSurfaceGap = ReelSurfaceGap;   // 初始值 = 导出配置；上钩改、脱钩/钓上重设
    }

    /// <summary>指定水区域（接触检测依赖它；不传则抛竿不会触发下沉、飞到超时兜底落地）。</summary>
    public void ConnectWater(Area2D? water)
    {
        _water = water;
        _space = null;
        _spaceResolved = false;
    }

    /// <summary>本钩所在水体的几何（供 FishContext 读距离/做探测）；没有可用水域 → null。
    /// Water 挂 WaterBody 时每帧直读它的 Space（地形 Rebuild/SetSpace 后自动生效，开销 O(1)）；
    /// 普通 Area2D 矩形只在首次查询时解析一次。</summary>
    internal WaterSpace? ResolveSpace()
    {
        if (_water is WaterBody body)
        {
            _space = body.Space;
            _spaceResolved = true;
            return _space;
        }
        if (_spaceResolved) return _space;
        _spaceResolved = true;
        _space = WaterBody.SpaceOf(_water);
        return _space;
    }

    /// <summary>
    /// 抛竿：power01 0..1 → 水平速度 MinSpeed..MaxSpeed，从场景抛出起点向右抛出。
    /// 上抛初速度按基准重力峰高反推，之后由 AirGravity×GravityBase 每帧加速。
    /// </summary>
    public void Cast(float power01)
    {
        GlobalPosition = _home;   // 回到抛出起点（上一竿可能沉在水里或停在岸上）
        _vx = Mathf.Lerp(MinSpeed, MaxSpeed, Mathf.Clamp(power01, 0f, 1f));
        _vy = -Mathf.Sqrt(2f * GravityBase * AirPeakHeight);   // 负 = 向上抛
        Rotation = Mathf.Atan2(_vy, _vx);
        _time = 0f;
        _radius = 0f;
        _parked = false;
        Current = State.Flying;
    }

    public override void _PhysicsProcess(double delta) => Step(delta);

    /// <summary>推进一帧。测试注入：headless 下把 ProcessMode 设 Disabled 后手动调用，结果完全确定。</summary>
    public void Step(double delta)
    {
        switch (Current)
        {
            case State.Flying: StepFlying(delta); break;
            case State.Sinking: StepSinking(delta); break;
            case State.Reeling: StepReeling(delta); break;
            case State.Hooked: StepHooked(delta); break;
        }
        SyncHookedFish((float)delta);   // 钩子本帧可能被拖动/收回/旋转：上钩的鱼重新对齐
    }

    /// <summary>上钩的鱼每帧与钩子对齐（由 Hook 驱动，鱼自己的物理步进已在上钩时关掉）：
    ///  ▪ 推进转身动画（左右翻转时精灵 Scale.X 在 FishDef.TurnDuration 内线性扫过 0）；
    ///  ▪ 鱼身整体转到钩子本帧的<b>移动方向</b>（鱼头指向运动方向，左右以竖直分界；钩子没动就保持）；
    ///  ▪ 把鱼嘴钉到钩子当前位置（鱼中心 = 钩子 − 嘴偏移，嘴偏移随鱼身旋转）——鱼不继承钩子自身的旋转。</summary>
    private void SyncHookedFish(float dt)
    {
        if (_hookedFish is not { } fish || !GodotObject.IsInstanceValid(fish)) return;
        fish.StepHooked(dt, new Vector2(_vx, _vy), GlobalPosition);
    }

    /// <summary>
    /// 落水后（Sinking）点击左键 → 进入收线模式：线长锁定 = 当前位置到抛出起点的距离，
    /// 之后钩子受重力 + 线长约束沿圆弧向起点正下方荡（飞行中/已结束调用无效——仅落水后生效）。
    /// </summary>
    public void BeginReel()
    {
        if (Current != State.Sinking) return;
        _radius = Mathf.Max((GlobalPosition - _home).Length(), 1f);   // 锁定线长（点击瞬间半径）
        _parked = false;
        _time = 0f;
        Current = State.Reeling;
    }

    /// <summary>
    /// 上钩：一条咬中的鱼接上钩子，进入 Hooked 状态。
    /// 前置：鱼必须是 Bitten（已咬中）、钩子必须在落水下沉 / 收线中（水里的钩才能被咬）、本钩已挂鱼线（Line != null）、尚未上钩。
    /// 不满足前置返回 false（咬中事件可被忽略，比如没挂鱼线的钩子——鱼就继续停着）。
    ///
    /// 上钩后：
    ///  ▪ 鱼节点成为钩子的子节点（一体），鱼停止自己的行为（Bitten 态本来就不动），后续完全由钩子带动；
    ///  ▪ 挂载锚点是<b>鱼嘴</b>（Fish.MouthWorldOffset）：鱼嘴钉在钩子上，鱼中心 = 钩子 − 嘴偏移；
    ///    钩子移动/旋转后每帧重新对齐（SyncHookedFish），鱼身按<b>钩子移动方向</b>倾斜（鱼头朝运动方向，
    ///    左右以竖直分界），不继承钩子自身的旋转；
    ///  ▪ 咬钩瞬间保持水平，先按拖行方向重算左右朝向（不再是咬钩瞬间的朝向）；真正开始移动后鱼身才倾斜，
    ///    左右翻转走 FishDef.TurnDuration 的转身动画；
    ///  ▪ 挣扎决策交给鱼种的 Struggle 策略（内置 ClassicStruggle 在 OnHooked 里按鱼种 + 鱼线注入
    ///    体力/耐力 = 满值、拉力 0、默认在挣扎），方向 = 远离抛出起点 + 每轮随机 ±60° 摆动；
    ///  ▪ Struggle 空槽 = 上钩后安静待收线，不拖钩子（打一条仅测试可见的警告）；
    ///  ▪ 挣扎中按住左键 = 施力（拖速减半、拉力上涨），松开 = 拖速全速、拉力回落；挣扎停后按住左键 = 正常收线拉回。
    /// </summary>
    public bool BeginHookUp(Fish fish)
    {
        if (fish == null) return false;
        if (fish.Current != Fish.State.Bitten) return false;                   // 只有真正咬中的鱼才上钩
        if (Current is not (State.Sinking or State.Reeling)) return false;     // 水里（下沉/收线中）的钩才可能被咬
        if (Current == State.Hooked) return false;
        if (Line == null) return false;                                        // 没挂鱼线：不咬钩

        // 一体：鱼从原父节点挪到钩子下面（Bitten 态鱼已停，之后跟着钩走）
        if (fish.GetParent() is Node oldParent) oldParent.RemoveChild(fish);
        AddChild(fish);
        fish.SetPhysicsProcess(false);   // 上钩后鱼不再自己步进：转身动画/朝向/挂载统一由本钩 SyncHookedFish 每帧驱动

        _hookedFish = fish;
        _retracting = false;
        _time = 0f;
        _radius = Mathf.Max((GlobalPosition - _home).Length(), 1f);
        Current = State.Hooked;

        // 咬钩后重算朝向：按首轮拖行方向（离开抛出起点）定鱼头朝向——上钩前朝哪边咬的作废，
        // 否则「向左咬钩 + 被往右拖」就会看到鱼倒着游。随后把鱼嘴（不是鱼中心）钉在钩上并抵消钩子旋转。
        PickPullDirection();
        fish.FaceAlong(_pullDir);
        fish.SnapMouthTo(GlobalPosition);

        // 上钩瞬间：鱼定义里配置的收线水面间隙覆盖当前值（数据驱动；-1 = 不改，沿用当前值）。
        // 在挣扎策略 OnHooked 之前写入——策略可用 FishContext.ReelSurfaceGap 再自行覆盖。
        if (fish.Def is { HookupSurfaceGap: >= 0f } defOverride)
            CurrentReelSurfaceGap = defOverride.HookupSurfaceGap;

        // 挣扎策略开场（数值注入由它负责：内置 ClassicStruggle 在这里 BeginFight 复位体力/耐力/拉力）
        if (fish.Def?.Struggle != null)
        {
            fish.Def.Struggle.OnHooked(new FishContext(this));
        }
        else
        {
            GD.PushWarning($"[Hook] 上钩的这尾鱼（{fish.Def?.DisplayName ?? fish.Name}）没有配置 Struggle 策略：上钩后安静待收线。");
        }

        return true;
    }

    /// <summary>一轮挣扎开始时选拖行方向：主方向 = 远离抛出起点，再随机偏转 ±60°（每轮挣扎换一次方向）。</summary>
    internal void PickPullDirection()
    {
        var away = GlobalPosition - _home;
        var baseDir = away.Length() > 0.01f ? away.Normalized() : Vector2.Right;   // 恰好在起点上 → 右（兜底）
        _pullDir = baseDir.Rotated(Mathf.DegToRad(_rng.RandfRange(-60f, 60f)));      // ±60° 内摆动，仍保证径向朝外
        _pullDir = _pullDir.Normalized();
    }

    private void StepFlying(double delta)
    {
        var dt = (float)delta;
        _time += dt;
        // 重力加速度（AirGravity×GravityBase）与重量无关——g 恒定、质量只改变阻力项：
        // 更重 → AirResist×vy/√Weight 越小 → 越接近自由落体（下落更快）；更轻 → 阻力相对越大 → 明显更慢。
        _vy += AirGravity * GravityBase * dt;
        _vy -= AirResist * _vy * dt / WeightFactor;
        GlobalPosition += new Vector2(_vx, _vy) * dt;         // 速度积分位移
        Rotation = Mathf.Atan2(_vy, _vx);                     // 朝向 = 轨迹切线

        ClampToLineLength();   // 抛出最远距离受线长限制（没挂鱼线则不限距）

        if (InWater()) { Current = State.Sinking; _time = 0f; return; }   // 触水 → 入水
        if (_time >= MaxAirTime) FinishRest();                // 一直没碰到水 → 兜底落地
    }

    private void StepSinking(double delta)
    {
        var dt = (float)delta;
        _time += dt;
        var wf = WeightFactor;   // √Weight：更重 → 水中阻力对它的减速影响更小 → 终端速度更高、漂得更远更慢停

        // 垂直：与空中同构的「正常重力 900 − 水中阻力」（需求：两环境唯一区别 = 阻力大小）——
        // dv/dt = AirGravity×GravityBase − (WaterResist/√W)·v。**先阻尼后加重力**：阻尼只作用于加重力前的速度，
        // 本帧新加的重力不会被按比例抹掉（否则终端会被系统性压低整整 g×δt：W=0.2 时会从 50.3 掉到 35.3）。
        // 入水无论带多大 _vy（空中攒下的）都会迅速刹到水中终端速度 ≈ 900×√W/WaterResist（默认 8 → W=1 ≈112.5 px/s）后匀速下潜（自然下沉不做单摆，直线竖直）。
        _vy -= WaterResist * _vy * dt / wf;
        _vy += AirGravity * GravityBase * dt;

        // 水平：同用水中阻力阻尼（漂移随阻力快速停稳），极小速度直接钳 0 视为漂停。
        _vx *= Mathf.Exp(-(WaterResist / wf) * dt);
        if (_vx < 0.5f) _vx = 0f;

        GlobalPosition += new Vector2(_vx, _vy) * dt;
        Rotation = _vx > 1f ? Mathf.Atan2(_vy, _vx) : Mathf.Pi / 2f;   // 水平停稳后垂直朝下

        ClampToLineLength();   // 未收线时不再受线松开约束、会一直下降，但钩子到起点距离不能超过线长（最远下沉深度）

        if (!InWater()) { ParkAtBottom(); return; }          // 穿出水底 → 停在水底，等收线（一竿不算结束）
        if (_time >= MaxSinkTime) FinishRest();              // 极端超时兜底
    }

    /// <summary>
    /// 收线模式逐帧推进（Sinking 落水后点击左键进入，或从沉底/正下方停住状态继续）：
    ///  ▪ 按住左键（ReelHeld）→ 收线（见 StepReelingPull）：进入拉升区（|X−锚点X| ≤ LiftUnlockRange）→ 单摆+重力
    ///    （无论多深都受重力，横向走摆动公式、线每帧缩短）；太浅或水上 → 空气重力圆弧收线（一律空中）；
    ///    骑到 X（±10%）保持高度水平移向锚点 / 深处直线朝锚点。收线期间线长只随收线缩短；骑线/直线/圆弧不穿水底；
    ///  ▪ 松开左键 → 立刻恢复重力：钩子沿「以起点为圆心、当前线长（收线后的新线长）为半径」的单摆圆弧
    ///    荡向起点正下方（水中过阻尼 → 落到最低点就停、不摆回）；途中先碰到水底 → 停在水底（重新压回水底）；
    ///    荡到正下方 → 停住；两个停住终点都不结束一竿，随时可再按住左键收线；
    ///  ▪ 一竿真正结束：收线收到距起点 &lt; ReelFinishDistance → Rest（完成收线）。
    /// </summary>
    private void StepReeling(double delta)
    {
        var dt = (float)delta;
        _time += dt;

        if (ReelHeld)
        {
            _parked = false;   // 一旦开始收线就离开停住态；随后松开左键会重新按重力摆动/下坠
            StepReelingReel(dt);
            return;
        }

        if (_parked) { Rotation = Mathf.Pi / 2f; return; }   // 已停住（沉底/正下方），等玩家再按住左键收线

        // ---- 放线摆动：单摆模型（真实摆动公式，大角度数值积分）----
        // 切向动力学：p = 钩子相对锚点、R = 线长（松开时不收线 → 锁定不变）；t = (−p.y, p.x)/|p| 为弧上逆时针单位切向。
        // vT = v·t（径向分量每帧被「钉回圆」舍去，只剩切向 = 沿弧滑动）；dvT/dt = g·(p.x/R) − (damp/√W)·vT，
        // 其中 g·(p.x/R) = 重力沿切向的分量 = g·sinθ（θ = 与竖直线的夹角）：**底端 p.x≈0 → 无切向加速度**，
        // 天然不会出现旧「勾股 + 垂直重力」在底端越摆越快的问题。
        // damp：水中 WaterResist/√W（默认 8 → 过阻尼，荡到最低点就停、不摆回另一边）；出水面改回空气 AirResist/√W（需求：出水即换空气）。
        var p = GlobalPosition - _home;
        var rT = Mathf.Max(p.Length(), 0.001f);
        var tan = new Vector2(-p.Y, p.X) / rT;   // 弧上逆时针单位切向
        var vT = _vx * tan.X + _vy * tan.Y;
        var g = AirGravity * GravityBase;
        var damp = InWater() ? WaterResist : AirResist;
        vT -= damp * vT * dt / WeightFactor;   // 水中超大阻力 → 过阻尼停底（先阻尼后加重力：本帧重力增量不被抹掉）
        vT += g * (p.X / rT) * dt;             // 重力切向分量（900 全量，底端=0）

        var pos = GlobalPosition + tan * (vT * dt);
        var dir = pos - _home;                 // 线长约束：距离恒等于当前线长
        var dist = dir.Length();
        if (dist > 0.001f) pos = _home + dir / dist * _radius;   // 钉回固定半径圆上（线长锁定；径向误差被舍去）

        _vx = dt > 0f ? (pos.X - GlobalPosition.X) / dt : 0f;   // 速度 = 本帧实际位移（天然只含切向分量）
        _vy = dt > 0f ? (pos.Y - GlobalPosition.Y) / dt : 0f;
        GlobalPosition = pos;
        Rotation = Mathf.Atan2(_vy, _vx);

        // ---- 停住终点 ----
        var floor = FloorY();
        if (floor.HasValue && GlobalPosition.Y >= floor.Value)
        {
            ParkAtBottom();                                           // 弧线中途先碰到水底 → 停在水底
            return;
        }
        if (Mathf.Abs(GlobalPosition.X - _home.X) < ReelVerticalThreshold)
        {
            // 荡到起点正下方 → 线竖直停住（最低平衡点；水中过阻尼本就会停这，此处钉住兜底）
            GlobalPosition = new Vector2(_home.X, _home.Y + _radius);
            _vx = 0f;
            _vy = 0f;
            _parked = true;
            Rotation = Mathf.Pi / 2f;
        }
    }

    /// <summary>
    /// 按住左键时的收线（三档 + 最终拉升，见 <see cref="StepReelingPull"/>）：收完（距起点 &lt; ReelFinishDistance
    /// 或已抵达锚点）→ 钩子收回起点、一竿结束。本帧速度同步到 _vx/_vy：松开左键后摆动从当前速度继续。
    /// </summary>
    private void StepReelingReel(float dt)
    {
        if (StepReelingPull(dt, ReelSpeed))
        {
            GlobalPosition = _home;
            FinishRest();
        }
    }

    /// <summary>
    /// 收线的一帧（自由收线 <see cref="StepReelingReel"/>、上钩回拉 StepHooked、脱钩自动收线 StepRetract 共用），
    /// 返回 true = 已收完、钩子已抵达锚点（调用方决定 FinishRest / CatchFish）。每帧按优先级：
    ///  ▪ 拉升区（|钩子X − 锚点X| ≤ LiftUnlockRange）→ 单摆收线 <see cref="LiftZoneSwing"/>：**无论钩子多深都受重力**
    ///       （带内/更深 = 水中重力；太浅/水上 = 空气重力），横向由单摆公式推进、线每帧缩短 speed×dt——取代旧的无视重力
    ///       直线拉升（需求 6：进入拉升区不再直线穿水面上提）；
    ///  ▪ 太浅/水上（深度 &lt; X×0.9）→ 圆弧收线 <see cref="SinkWhileReeling"/>：线长每帧缩短 speed×dt，
    ///       **一律按空气重力下摆**（需求 5：太浅无论水下/水上统一视为空气重力，不再分水/气），勾股钉回
    ///       「以锚点为圆心、当前线长为半径」的圆弧上——钩子边被收近边受重力向下摆，绝不无视重力直线收；
    ///  ▪ 深处（深度 &gt; X 容差带上沿）→ 直线朝锚点（不受重力、不受 X，旧行为）；
    ///  ▪ 带内（深度 ∈ [X×0.9, X×1.1]）→ 保持高度、水平移向锚点（骑线）。
    /// 无水域 / X ≤ 0 → 兜底直线拉回。直线/骑线全程钳制不穿水底。speed = 收线速度（玩家 ReelSpeed / 脱钩 AutoRetractSpeed）。
    /// </summary>
    private bool StepReelingPull(float dt, float speed)
    {
        var space = ResolveSpace();
        if (space == null) return PullTowardHome(dt, speed);            // 无水域 → 兜底旧直线行为

        var bandMax = CurrentReelSurfaceGap * (1f + SurfaceGapTolerance);
        if (bandMax < 0f) return PullTowardHome(dt, speed);            // X ≤ 0 → 退化为旧行为
        var depth = GlobalPosition.Y - space.SurfaceY;
        var bandMin = CurrentReelSurfaceGap * (1f - SurfaceGapTolerance);

        if (Mathf.Abs(GlobalPosition.X - _home.X) <= LiftUnlockRange)   // ① 拉升区（任何深度）：单摆 + 重力 + 线缩短
            return LiftZoneSwing(dt, speed);
        if (depth < bandMin) return SinkWhileReeling(dt, speed);        // ② 太浅/水上：一律空气重力圆弧
        if (depth > bandMax) return PullTowardHome(dt, speed);          // ③ 深处：直线朝锚点
        return RideTowardHome(dt, speed);                               // ④ 带内：骑线
    }

    /// <summary>
    /// 拉升区内收线一帧——单摆 + 线长每帧缩短（需求 6）：横向位移用放线摆动同款单摆公式（切向重力 + 阻尼 + 钉回圆），
    /// 线长每帧缩短 speed×dt（gravity 永不把线拉长）。重力环境按深度分：带内/更深 → 水中重力（WaterResist，过阻尼停底）；
    /// 太浅/水上 → 空气重力（AirResist，与太浅档一致）。线收完（≤ ReelFinishDistance）→ 收至锚点返回 true。
    /// </summary>
    private bool LiftZoneSwing(float dt, float speed)
    {
        var rNew = Mathf.Max(_radius - speed * dt, 0f);          // 收线 → 线长只减不增
        if (rNew <= ReelFinishDistance)                          // 线已收完：钩子回到锚点，一竿结束
        {
            GlobalPosition = _home;
            _radius = 0f;
            _vx = 0f;
            _vy = 0f;
            Rotation = 0f;
            return true;
        }

        var p = GlobalPosition - _home;
        // 已垂下正下方（|p.x| ≤ 阈值）：不再切向摆荡，改竖直收回锚点——单摆越靠近底端切向重力→0、
        // 而浅水段空气阻尼弱，残余切向速度会带着钩子荡过锚点线到另一侧再被拖回（x 折返/视觉上越过正下方）。
        // 垂挂后直接直线收：只走本侧、绝不荡过锚点线（需求「水中过阻尼不摆到另一边」的显式落地）。
        if (Mathf.Abs(p.X) <= ReelVerticalThreshold)
            return PullTowardHome(dt, speed);

        var rT = Mathf.Max(p.Length(), 0.001f);
        var tan = new Vector2(-p.Y, p.X) / rT;                   // 弧上逆时针单位切向
        var vT = _vx * tan.X + _vy * tan.Y;
        var g = AirGravity * GravityBase;
        var space = ResolveSpace();
        var depth = space != null ? GlobalPosition.Y - space.SurfaceY : float.MaxValue;
        var bandMin = CurrentReelSurfaceGap * (1f - SurfaceGapTolerance);
        // 重力环境：带内/更深（≥ X×0.9）→ 水中；太浅/水上（< X×0.9）→ 空气（与太浅档的规则一致，需求 6）
        var damp = depth < bandMin ? AirResist : WaterResist;
        vT -= damp * vT * dt / WeightFactor;                     // 阻尼（先阻尼后加重力：切向终端不被压低 g×dt）
        vT += g * (p.X / rT) * dt;                               // 重力切向分量（底端=0）

        var pos = GlobalPosition + tan * (vT * dt);
        var dir = pos - _home;
        var dist = dir.Length();
        if (dist > 0.001f) pos = _home + dir / dist * rNew;      // 钉回到缩短后的新线长圆弧上

        var floor = FloorY();                                    // 圆弧钻进水底 → 停在水底边缘（线仍继续缩短）
        if (floor.HasValue && pos.Y > floor.Value) pos.Y = floor.Value;
        // 兜底：单摆一帧带速过大也不越锚点线到另一侧（对侧再被拖回 = x 折返）。钳在「本帧原 x 与锚点 x」之间——
        // 本侧合法摆荡不受影响（如从左侧摆向正下方），只是逼近锚点线、绝不穿过它。
        pos.X = Mathf.Clamp(pos.X, Mathf.Min(_home.X, GlobalPosition.X), Mathf.Max(_home.X, GlobalPosition.X));

        _vx = dt > 0f ? (pos.X - GlobalPosition.X) / dt : 0f;
        _vy = dt > 0f ? (pos.Y - GlobalPosition.Y) / dt : 0f;
        _radius = rNew;                                          // 线长固定为缩短后的值（绝不回算放大）
        Rotation = Mathf.Atan2(_vy, _vx);
        GlobalPosition = pos;
        return false;
    }

    /// <summary>向锚点直线收线一帧（匀速 speed，不受重力、不受 X）。返回 true = 已到锚点（调用方收尾）。
    /// 本帧速度同步到 _vx/_vy：松开左键后摆动从当前速度继续（径向分量会被线长约束去掉）。</summary>
    private bool PullTowardHome(float dt, float speed)
    {
        var toHome = _home - GlobalPosition;
        var dist = toHome.Length();
        if (dist <= ReelFinishDistance) { GlobalPosition = _home; return true; }

        var shift = toHome.Normalized() * (speed * dt);
        if (shift.Length() >= dist) { GlobalPosition = _home; return true; }
        GlobalPosition += shift;

        var floor = FloorY();   // 兜底：极端参数/起点在水底以下时也不允许穿出水底
        if (floor.HasValue && GlobalPosition.Y > floor.Value)
            GlobalPosition = new Vector2(GlobalPosition.X, floor.Value);

        _vx = shift.X / dt;   // 记录本帧速度（松手后沿圆弧摆动从此速度继续）
        _vy = shift.Y / dt;
        _radius = (GlobalPosition - _home).Length();   // 线长随收线缩短
        Rotation = Mathf.Atan2(shift.Y, shift.X);
        return false;
    }

    /// <summary>骑线一帧：Y 冻结在进入容差带时的高度，水平（speed）移向锚点正下方。永不自己收完（收完交给拉升区）。</summary>
    private bool RideTowardHome(float dt, float speed)
    {
        var toHomeX = _home.X - GlobalPosition.X;
        var dir = toHomeX >= 0f ? 1f : -1f;
        var step = Mathf.Min(speed * dt, Mathf.Abs(toHomeX));
        var mk = GlobalPosition + new Vector2(dir * step, 0f);

        var floor = FloorY();
        if (floor.HasValue && mk.Y > floor.Value) mk.Y = floor.Value;   // 浅水区骑线也不穿底

        _vx = dt > 0f ? dir * (step / dt) : 0f;
        _vy = 0f;
        _radius = (mk - _home).Length();
        Rotation = Mathf.Atan2(_vy, _vx);
        GlobalPosition = mk;
        return false;
    }

    /// <summary>
    /// 太浅/水上收线一帧——圆弧下沉模型（需求 5）：收线只会让线变短，**重力永远不能让线变长**。
    /// 每帧先把线长缩短 speed×dt（钳到 ≥0）→ 推进垂直速度——**一律按空气重力（AirGravity×GravityBase − AirResist×v/√W）**：
    /// 太浅无论在水下还是水上都视为「受空中重力」（用户明确改的统一规则，不再区分水/气）→
    /// 垂直偏移 = 上一帧相对锚点的垂直 + 本帧重力增量，钳制 |v| ≤ 新线长（线不会把钩拉过锚点）→
    /// 按勾股定理水平 = √(线长² − v²)，方向朝当前钩子那一侧（太浅时线几近水平，勾股即既得水平分量）。
    /// 于是钩子一边被线收近、一边受重力向下摆（相对锚点），位置每帧独立算，一直在「以锚点为圆心、当前线长为半径」的圆弧上。
    /// 线长收完（≤ ReelFinishDistance）→ 钩子收至锚点、返回 true（一竿结束）。圆弧钻进水底 → 停在水底边缘继续收线。
    /// </summary>
    private bool SinkWhileReeling(float dt, float speed)
    {
        var rNew = Mathf.Max(_radius - speed * dt, 0f);          // 收线 → 线长只减不增
        if (rNew <= ReelFinishDistance)                          // 线已收完：钩子回到锚点，一竿结束
        {
            GlobalPosition = _home;
            _radius = 0f;
            _vx = 0f;
            _vy = 0f;
            Rotation = 0f;
            return true;
        }

        _vy -= AirResist * _vy * dt / WeightFactor;     // 一律空气重力 900 + 空气阻力（需求 5：太浅=视为水上，不区分水下/水上）
        _vy += AirGravity * GravityBase * dt;

        // 相对锚点的垂直偏移（向下为正）= 上一帧垂直分量 + 本帧重力增量，钳到新线长内；
        // 水平按勾股定理从弧上补出（方向 = 当前钩子所在的一侧），两轴合成 → 钉在圆弧上
        var relV = Mathf.Clamp((GlobalPosition.Y - _home.Y) + _vy * dt, -rNew, rNew);
        var relH = Mathf.Sqrt(Mathf.Max(rNew * rNew - relV * relV, 0f));
        var dirX = GlobalPosition.X >= _home.X ? 1f : -1f;
        var mk = _home + new Vector2(dirX * relH, relV);

        var floor = FloorY();   // 圆弧钻进水底 → 停在水底边缘（线长仍继续缩短，弧随线变短会离开水底）
        if (floor.HasValue && mk.Y > floor.Value) { mk.Y = floor.Value; _vy = 0f; }

        _vx = dt > 0f ? (mk.X - GlobalPosition.X) / dt : 0f;   // 记录实际水平位移（松手后沿弧摆动从当前速度继续）
        _radius = rNew;                                        // 线长固定为缩短后的值（绝不回算放大）
        Rotation = Mathf.Atan2(_vy, _vx);
        GlobalPosition = mk;
        return false;
    }

    /// <summary>沉出水底（或弧线碰到水底）→ 停在水底边缘，转入收线模式的停住态：一竿不算结束，随时可按住左键收线。</summary>
    private void ParkAtBottom()
    {
        var floor = FloorY();
        if (floor.HasValue)
            GlobalPosition = new Vector2(GlobalPosition.X, floor.Value);
        _vx = 0f;
        _vy = 0f;
        _radius = Mathf.Max((GlobalPosition - _home).Length(), 1f);   // 停住后线长 = 当前位置到起点（未来收线距离）
        _parked = true;
        _time = 0f;
        Rotation = Mathf.Pi / 2f;
        Current = State.Reeling;
    }

    /// <summary>当前 x 处的水底 y（世界坐标）：WaterBody 湖床折线优先；普通矩形 → 矩形底；都没有 → null。</summary>
    private float? FloorY()
    {
        var space = ResolveSpace();
        if (space != null) return space.FloorYAt(GlobalPosition.X);
        var rect = WaterRect();
        return rect.HasValue ? rect.Value.Position.Y + rect.Value.Size.Y : null;
    }

    /// <summary>
    /// 上钩状态逐帧推进（BeginHookUp 进入）：
    ///  ▪ 每帧的「挣扎决策」委托给鱼种的 Struggle 策略（内置 ClassicStruggle = 体力/耐力消耗、
    ///    拉力升落、停止/再挣扎、每轮重选拖行方向）；自定义策略可完全接管，空槽 = 不决策（安静）；
    ///  ▪ 决策结果只有三个输出：PullVelocity（拖行速度；零 = 不拖）、ReleaseRequested（主动脱钩）与
    ///    PlaybackState（鱼的动画播放状态）；Hook 照单执行——先查脱钩出口（主动脱钩 / 被拖到线长极限）
    ///    再动钩子，并把 PlaybackState 应用到鱼（SpeedScale）；
    ///  ▪ 不拖时：按住左键 = 正常收线拉回，收回起点 → 钓上（Caught）；松开 = 原地悬停等玩家；
    ///  ▪ 脱钩两个出口：数值机 Released / 自定义 ReleaseRequested，或 被拖到线长极限 → 鱼消失、线自动收回。
    /// </summary>
    private void StepHooked(double delta)
    {
        var dt = (float)delta;
        _time += dt;

        if (_retracting) { StepRetract(dt); return; }   // 脱钩后收线中：只回起点，不再管鱼

        var line = Line;   // 上钩后鱼线取消/置换 → 视为线没了 → 脱钩
        if (line == null) { ReleaseFish(); return; }

        // ---- 挣扎决策：鱼种的 Struggle 策略（空槽 = 不决策 → PullVelocity 停 0 → 安静可收线） ----
        var ctx = new FishContext(this);
        if (_hookedFish is { } fish && fish.Def?.Struggle != null) fish.Def.Struggle.Update(ctx, delta);

        // ---- 脱钩出口 1：挣扎实现请求脱钩（默认 = 拉力拉断） 或 2：被拖到线长极限 ----
        if (ctx.ReleaseRequested || LineOut >= line.Length)
        {
            ReleaseFish();   // 鱼直接消失、装备清空、鱼线缓慢自动收回
            return;
        }

        _hookedFish?.ApplyPlayback(ctx.PlaybackState);   // 动画播放状态（挣扎/虚弱/正常 → 切 SpeedScale）

        if (ctx.PullVelocity.LengthSquared() > 0.0004f)   // > 0.02 px/s 视为在拖
        {
            // 挣扎：沿实现给出的速度把钩子带走（按住左键施力 → 拖速减半、拉力上涨由实现决定）；
            // 位移先过一遍水体边界（撞水面/岸/水底 → 沿边滑），保证鱼不会把钩拖出水体；这一帧就到线长 → 直接脱钩
            var pull = ctx.PullVelocity * dt;
            var space = ResolveSpace();
            var move = space != null ? space.MoveInside(GlobalPosition, pull) : pull;
            var mk = GlobalPosition + move;
            if ((mk - _home).Length() >= line.Length) { ReleaseFish(); return; }
            ApplyMove(mk, dt);
            return;
        }

        // ---- 挣扎停/不拖：按住左键 = 收线拉回（三档 + 最终拉升，见 StepReelingPull；拖回鱼 → 钓上）；松开 = 原地悬停 ----
        if (ReelHeld)
        {
            if (StepReelingPull(dt, ReelSpeed)) { CatchFish(); return; }
            return;
        }

        _vx = 0f;   // 安静悬停：无速度（供调试标签/鱼线曲度读数）
        _vy = 0f;
    }

    /// <summary>脱钩（拉力拉断/拉到线长极限）：鱼直接消失（不回到出生点）、钩子/鱼饵/铅坠全部清空、鱼线缓慢自动收回起点（收完一竿结束）。</summary>
    private void ReleaseFish()
    {
        ClearHookedFish();
        Struggle.Tension = 0f;                       // 脱钩：拉力归零（拉断/线长极限残留的拉力不带到调试标签与下一竿）
        CurrentReelSurfaceGap = ReelSurfaceGap;   // 脱钩：收线水面间隙重设回初始值
        HookDef = null;      // 脱钩：装备全清
        Bait = null;
        LiveBait = null;
        Sinker = null;
        _retracting = true;   // 线慢慢收回去（钩子跟着回起点）
    }

    /// <summary>收线把鱼安全收回起点 → 钓上：只清空真饵与活饵（鱼钩、铅坠保留），触发 Caught 交给调用方生成放置物。</summary>
    private void CatchFish()
    {
        var fish = _hookedFish;
        if (fish != null)
        {
            fish.GlobalPosition = GlobalPosition;   // 鱼跟着到起点
            Bait = null;        // 钓上：只去掉钓饵（钩子/配重保留可复用）
            LiveBait = null;
            Caught?.Invoke(fish);   // 先发事件（MainStage 读 fish.Def.ItemDef 生成物品），再清鱼节点
        }
        Struggle.Tension = 0f;                       // 钓上：拉力同样归零
        CurrentReelSurfaceGap = ReelSurfaceGap;   // 钓上：收线水面间隙重设回初始值
        ClearHookedFish();
        FinishRest();
    }

    /// <summary>脱钩后自动收线：遵循与按住左键收线相同的三档规则（见 <see cref="StepReelingPull"/>，
    /// 水平速度用 AutoRetractSpeed），回到抛出起点 → 一竿结束（Rest）。</summary>
    private void StepRetract(float dt)
    {
        if (StepReelingPull(dt, AutoRetractSpeed)) FinishRest();
    }

    /// <summary>鱼节点拆离并消失：先移除出本钩子树（立即生效，测试友好），再调度释放。</summary>
    private void ClearHookedFish()
    {
        if (_hookedFish == null) return;
        var fish = _hookedFish;
        _hookedFish = null;
        if (GodotObject.IsInstanceValid(fish) && fish.GetParent() == this) RemoveChild(fish);
        fish.QueueFree();   // 鱼直接消失，不再回到出生点
    }

    /// <summary>移动钩子并记录本帧速度（供调试标签/鱼线曲度读数）：出界目标按当前速度补一段也不会越线。</summary>
    private void ApplyMove(Vector2 newPos, float dt)
    {
        var delta = newPos - GlobalPosition;
        GlobalPosition = newPos;
        _vx = dt > 0f ? delta.X / dt : 0f;
        _vy = dt > 0f ? delta.Y / dt : 0f;
        if (delta.LengthSquared() > 0.25f) Rotation = Mathf.Atan2(delta.Y, delta.X);
    }

    /// <summary>鱼线长度上限：钩子到抛出起点的距离不得超过线长（抛出最远距离 / 最深下沉距离）。
    /// 没挂鱼线（Line == null）不限距——保持旧行为。</summary>
    private void ClampToLineLength()
    {
        if (Line == null) return;
        var fromHome = GlobalPosition - _home;
        var dist = fromHome.Length();
        if (dist <= Line.Length) return;
        GlobalPosition = _home + fromHome / dist * Line.Length;   // 推回线长半径圆上
    }

    private void FinishRest()
    {
        Current = State.Rest;
        _retracting = false;
        Finished?.Invoke();
    }

    /// <summary>当前位置是否在水区域内：Water 挂 WaterBody → 用烘焙好的水体几何（含湖床折线）判定；
    /// 普通未旋转矩形 → 全局包围盒点判定（确定性、测试友好）；旋转/异形 → 回退物理查询。</summary>
    private bool InWater()
    {
        if (_water == null || !GodotObject.IsInstanceValid(_water)) return false;
        var space = ResolveSpace();
        if (space != null) return space.IsWater(GlobalPosition);
        var rect = WaterRect();
        return rect != null ? rect.Value.HasPoint(GlobalPosition) : _water.OverlapsArea(this);
    }

    /// <summary>Water 的矩形碰撞体（未旋转）全局包围盒；找不到/旋转/异形时返回 null → 走物理查询。</summary>
    private Rect2? WaterRect()
    {
        if (_water == null || !GodotObject.IsInstanceValid(_water)) return null;
        if (_water.GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return null;
        if (cs.Shape is not RectangleShape2D rectShape || cs.Rotation != 0f) return null;
        var size = rectShape.Size * cs.GlobalScale;
        return new Rect2(cs.GlobalPosition - size / 2f, size);
    }
}
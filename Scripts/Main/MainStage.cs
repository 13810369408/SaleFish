using Godot;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.Localization;
using SaleFish.Save;
using SaleFish.View;

namespace SaleFish.Main;

/// <summary>
/// 正式主关卡（main_scene，见 scenes/main_stage.tscn）：
/// 「获取放置物 → 用仓库收放 → 丢弃」的最小可玩流程，全部复用现有解耦层
/// （数据层 SpatialInventory / 视图层 InventoryView / 全局手 HeldItemController）。
///
/// 场景结构分层（世界 vs UI）：
///  ▪ 根节点 Node2D = 世界：宝箱图标 Sprite2D、Camera2D、未来的鱼/装饰等世界对象都挂这里（随镜头滚动）；
///  ▪ UI（CanvasLayer layer=1）下的全屏 Hud（Control）= 界面：按钮/状态/仓库面板挂这里，镜头动不到。
///    本脚本在 _Ready 里动态把按钮和状态栏挂进 Hud（UI/Hud），仓库打开时也以 Hud 为父节点。
///
/// 按钮状态机：
///  ▪ 获取放置物：始终可见；手持时禁用（手持无法再获取新放置物）。
///  ▪ 丢弃放置物：仅在「拿到物品」之后出现。
///  ▪ 仓库开关 = 点击场景里的仓库按钮（挂 WarehouseButton 组件的 Sprite2D，现为「Inventory」宝箱，始终可见，
///    不随仓库隐藏）：每个按钮开/关它自己配置的那座仓库（见 scripts/view/WarehouseButton.cs，多仓库就多挂几个
///    组件、各配各的 Storage）。命中路由在本类 _UnhandledInput（调按钮的 Toggle），模态规则不拦按钮自身的开关。
///  ▪ 钓鱼锁定：开始蓄力（抛竿进度条出现）即进入「正在钓鱼」状态（GamePhase.Fishing），到「钓上」或「一竿结束（Rest）」
///    才退回——这段时间点击仓库按钮只被吞掉、不开仓库（防钓鱼过程误弹仓库），见 GamePhase 状态机。
///  ▪ 空手点仓库背景/空格子按住左键 = 整座仓库跟随鼠标拖走，下次打开默认还在该位置（见 InventoryView 拖拽 +
///    WarehouseButton 位置记忆）。
///  ▪ 钓上鱼时：除生成放置物外，还自动打开绑定的仓库按钮所对应的仓库（见 AutoOpenWarehouseButtonPath）。
///
/// 钓鱼流程：空手且鱼钩未抛出时 点击左键 开始蓄力（钓竿力度的进度条显示在 Player 节点顶端，
/// 三角波 0→100→0，见 ChargeRatio），再次点击左键 → 钩子按「蓄力值经 [MinChargeValue, MaxChargeValue]
/// 窗口归一（ChargeToPower）后的抛投力度 0..1」线性映射水平速度 MinSpeed..MaxSpeed 向右抛物线抛出，
/// 蓄力条隐藏（显示与力度同用该归一：< MinChargeValue 停在 0、> MaxChargeValue 停在满值，最近/最远好抛）；接触 Water（Area2D 区域）后缓慢下沉；
/// 落水后按住左键进入收线模式（钩子受重力+鱼线约束向起点正下方画弧；按住左键以「收线速度」收回，
/// 见 scripts/fishing/Hook.cs 的 Reeling 状态）；收线收完回到起点 → 一竿结束。全程
/// （铁沉水底/荡到正下方停住）都不结束一竿，随时可继续收线。一竿结束（钓上或断线收回到家、可再次抛竿时）
/// 镜头在 1 秒内从跟随位置平缓回到抛竿前记录的初始位置（抛竿前在 StartCharging 记录）。
///
/// 鱼上钩（见 Hook.Hooked / FishStruggle）：鱼咬中 → FishSpawner.FishBitten 广播 → 本关接 Hook.BeginHookUp
/// （鱼挂到钩子下面，挣扎 = 拖着钩远离抛出起点，按住左键施力拖速减半 + 拉力上涨）；脱钩（拉力拉断或拉到线长）→
/// 鱼消失 + 装备全清 + 鱼线自动收回；钓上（挣扎停后收线拉回起点）→ 读鱼的 ItemDef 生成放置物放到鼠标上。
/// ShowTestText 一键开关的调试文字在顶部居中显示 体力/耐力/拉力/线长 实况。
///
/// 随机池：检查器 RandomPool 自由配置；为空时回退扫 res://Data/Inventory/Items/，目录缺失再退回内置占位集。
/// 操作：左键 拿起/放下 · 右键 旋转 90° · 点击宝箱图标 开关仓库 · 空手左键 抛竿→再点抛出 · 落水后按住左键 收线/施力
///
/// 仓库模态（打开中的仓库以 WarehouseRegistry.OpenCount 统一判定，见 scripts/view/WarehouseRegistry.cs）：
///  ▪ 单仓库打开 + 空手：左键点击仓库页面以外 = 关闭当前仓库（不再落入抛竿/世界互动）；
///  ▪ 单仓库打开 + 手持放置物：点击仓库以外不关闭（放/拾只在仓库页内生效），也不抛竿；
///  ▪ 多仓库同时打开（≥2）：点击仓库以外一律不生效（禁止与其他物品/世界互动），仅「打开/关闭」按钮（宝箱图标）
///    仍可隐藏它所对应的仓库。
/// </summary>
public sealed partial class MainStage : Node2D
{
    /// <summary>
    /// 行为阶段状态机（当前两态；后续扩展新状态只需加枚举值 + 在对应入口/出口补 SetPhase 迁移与各自的门）：
    ///  ▪ Ready ——「准备钓鱼/空闲」：仓库按钮点击正常开关仓库；
    ///  ▪ Fishing ——「正在钓鱼」：从开始蓄力（抛竿进度条出现）起进入，到「钓上」或「一竿结束（Rest）」为止；
///    蓄力中按右键取消抛竿（CancelCharging）会立即退回 Ready。
    ///    这段时间仓库按钮点击被吞掉（SetInputAsHandled 但不开关），防止钓鱼过程中误把仓库弹出来。
    /// </summary>
    public enum GamePhase
    {
        /// <summary>准备钓鱼（站在钓鱼点，空闲）：空手左键抛竿；「退出钓鱼」→ FreeMove。</summary>
        Ready,
        /// <summary>正在钓鱼（蓄力 → 钓上/一竿结束/右键·菜单取消）。</summary>
        Fishing,
        /// <summary>自由移动：A/D 左右走（只有这里能移动玩家），走回钓鱼点点击坐下 → Ready。</summary>
        FreeMove,
    }

    /// <summary>当前行为阶段（公开只读，测试断言用）。</summary>
    public GamePhase Phase { get; private set; } = GamePhase.Ready;

    /// <summary>切换行为阶段（同值跳过，幂等）。</summary>
    private void SetPhase(GamePhase next)
    {
        if (Phase == next) return;
        Phase = next;
    }

    /// <summary>钓上鱼时自动打开的仓库按钮（路径）：场景里的一个挂 WarehouseButton 组件的仓库开/关节点
    /// （当前是 Sprite2D「Inventory」宝箱）。钓上时对它走「打开仓库」（调它的 Open，仅打开不关闭）；
    /// 以后场景有多个仓库按钮时，把这里指向你想让钓上自动弹出的那个。
    /// null/空路径/节点无效 = 钓上不自动打开（保持旧行为：只生成放置物）。仓库被拖拽过位置后，自动打开也落在该位置。</summary>
    [Export] public NodePath? AutoOpenWarehouseButtonPath = new NodePath("Inventory");

    /// <summary>钓上自动开仓的按钮路径已解析失败过一次（只告警一次，避免每次钓上刷屏）。</summary>
    private bool _warnedAutoOpenMissing;

    /// <summary>界面挂载点（场景 UI 层下的全屏 Hud 控件）：按钮/状态/仓库面板都挂这里，CanvasLayer 不受镜头影响。</summary>
    private Control? _hud;

    /// <summary>右上角菜单组件（MenuUi 面板）：订阅它的 Opened，做到「蓄力中点菜单先取消抛竿」。</summary>
    private MenuUi? _menuUi;

    private Button _exitFishButton = null!;   // 「退出钓鱼」：仅 Ready 阶段显示 → 切 FreeMove
    private Label _status = null!;
    private Label _helpLabel = null!;   // 底部操作帮助（随语言切换刷新）
    private Label _speedLabel = null!;   // 右上角实时速度（水平/垂直，调试参考，随 _Process 每帧刷新）

    // ---- 自由移动（GamePhase.FreeMove）：A/D 左右走，CharacterBody2D 踩地面；走回钓鱼点点击坐下 ----
    /// <summary>玩家行走速度（px/s）。</summary>
    [Export] public float WalkSpeed = 220f;
    /// <summary>玩家下落重力（px/s²）：CharacterBody2D 落回地面的简单物理。</summary>
    [Export] public float WalkGravity = 1200f;
    private CharacterBody2D? _player;        // 玩家物理体（场景 Player）
    private Sprite2D? _playerSprite;         // 玩家视觉（Player/Sprite，蓄力条锚点用）
    private FishingSpot? _fishSpot;          // 钓鱼点（场景 FishingSpot）
    private bool _walkingToSpot;             // 正在自动走到钓鱼点
    private float _walkTargetX;              // 走去坐下的目标 X
    private Label _sitFishLabel = null!;     // 「坐下钓鱼」悬浮提示（跟随鼠标）

    // ---- 钓鱼（蓄力/抛出/收线，无钓竿按钮——空手左键即抛竿） ----
    private Hook _hook = null!;
    private Area2D? _water;
    private FishSpawner? _spawner;   // 鱼群：咬中事件从这里接上钩逻辑
    private ProgressBar _powerBar = null!;
    private bool _charging;           // 是否正在蓄力（此时显示蓄力条，再点一次左键即抛出）
    private double _chargeElapsed;    // 蓄力已进行时间（秒）
    /// <summary>上钩测试文字（体力/耐力/拉力/线长）标签；ShowTestText 为 false 时不创建。</summary>
    private Label _struggleLabel = null!;

    // ---- 镜头跟随 ----
    /// <summary>镜头缓动时长的基准（秒）：抛出后把鱼钩平滑带到画面中央；一竿结束（钓上/脱钩收回到起点）后
    /// 用同样时长从跟随位置平缓回到抛竿前记录的初始位置。</summary>
    public const double CameraFollowDuration = 1.0;

    private Camera2D _camera = null!;
    private bool _cameraFollowing;      // 抛竿后是否在跟随鱼钩
    private Vector2 _cameraFrom;        // 开始跟随时的镜头位置（平滑起点）
    private double _cameraElapsed;      // 跟随已持续时长（秒），前 CameraFollowDuration 秒做 easeOut 居中
    private bool _cameraReturning;      // 一竿结束后：镜头从跟随位置平缓回归初始位置中
    private Vector2 _cameraReturnFrom;  // 回归起点（一竿结束时镜头所在位置）
    private Vector2 _cameraRestPos;     // 回归目标 = 抛竿前记录的初始镜头位置（每次开始抛竿前记录）
    private double _cameraReturnElapsed;

    /// <summary>镜头是否正处在「一竿结束 → 平缓回归抛竿前初始位置」的动画中（公开只读，测试用）。</summary>
    public bool CameraReturning => _cameraReturning;

    // ---- 鱼线视觉（Line2D，钓竿到鱼钩的 6px 连续细线；下落时上拱、收线笔直） ----
    private Line2D _fishingLine = null!;
    /// <summary>弧线上拱量（世界像素）：每帧向目标收敛，收线时迅速归零 → 线转直。</summary>
    private float _lineSag;

    // ---- 鱼线弧线可调参数（检查器可见可改；几何/公式见 scripts/view/FishingLineCurve.cs） ----
    /// <summary>鱼线最大拱高（世界像素）：钩子极速下落时弧线顶端相对弦线的最大上拱量。</summary>
    [Export(PropertyHint.Range, "0,120,1")] public float LineMaxSag { get; set; } = 36f;
    /// <summary>钩子下落速度达到满拱的参考值（px/s）：越大越不肯弯（0 = 永不弯曲）。</summary>
    [Export(PropertyHint.Range, "50,1000,10")] public float LineSagFallSpeed { get; set; } = 450f;
    /// <summary>弧线变化速度（px/s）：收线拉直 / 过顶点起弯的速率，越大越干脆。</summary>
    [Export(PropertyHint.Range, "20,1000,10")] public float LineSagResponse { get; set; } = 240f;
    /// <summary>拱顶在弦上的位置（0..1，鱼钩端 = 1）：0.5 = 正中，越大越靠近鱼钩（下垂最松点偏下游）。</summary>
    [Export(PropertyHint.Range, "0.05,0.95,0.05")] public float LineSagPeak { get; set; } = 0.75f;
    /// <summary>拉力预警起点：拉力 ≥ 此值鱼线开始由白渐变变红（拉力 < 此值 = 纯白不预警）。</summary>
    [Export(PropertyHint.Range, "0,200,1")] public float LineTensionRedFrom { get; set; } = 50f;
    /// <summary>拉力全红点：拉力 ≥ 此值鱼线完全变红（濒临断线）。默认 100 = basic_line 的 MaxTension。</summary>
    [Export(PropertyHint.Range, "0,300,1")] public float LineTensionRedAt { get; set; } = 100f;

    // ---- 抛投蓄力窗口（0..100 刻度上的「力度死区」：让最近/最远都容易抛） ----

    /// <summary>抛投最小距离蓄力值：蓄力值 ≤ 此值都按「最近」（抛投力度 0）。默认 10 让最近占 0..10 一段停顿。</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float MinChargeValue = 10f;

    /// <summary>抛投最大距离蓄力值：蓄力值 ≥ 此值都按「最远」（抛投力度 1）。默认 90 让最远占 90..100 一段停顿。
    /// 想恢复旧 0..100 直连（蓄力值=抛投力度）就填 Min=0 / Max=100；Min &gt; Max 时自动视为两者对调。</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float MaxChargeValue = 90f;

    /// <summary>是否显示上钩测试文字（体力/耐力/拉力/线长，屏幕顶部居中）：调试用，一键开关；false 不创建标签。</summary>
    [Export] public bool ShowTestText { get; set; } = true;

    public override void _Ready()
    {
        // 视野范围 = 设计分辨率(project.godot 的 window/size/viewport_*) ÷ Camera2D.Zoom，与窗口/屏幕大小解耦。
        // 最大化：每台设备用自己的全部分辨率显示同一 1920×1080 世界（高分辨率屏更细腻，看到的内容不变）。
        GetWindow().Mode = Window.ModeEnum.Maximized;

        _player = GetNodeOrNull<CharacterBody2D>("Player");
        _playerSprite = _player?.GetNodeOrNull<Sprite2D>("Sprite");
        _fishSpot = GetNodeOrNull<FishingSpot>("FishingSpot");
        if (_player == null)
            GD.PushWarning("[MainStage] 找不到 Player（CharacterBody2D）：自由移动不可用。");
        if (_playerSprite == null)
            GD.PushWarning("[MainStage] 找不到 Player/Sprite：抛竿蓄力条将停在初始位置（不跟随玩家）。");

        _hud = GetNodeOrNull<Control>("UI/Hud");
        if (_hud == null)
            GD.PushWarning("[MainStage] 找不到 UI 层下的 Hud 控件：界面将挂回世界根并随镜头移动。");

        _hook = GetNodeOrNull<Hook>("Hook");
        _water = GetNodeOrNull<Area2D>("Water");
        _camera = GetNodeOrNull<Camera2D>("Camera2D");
        _fishingLine = GetNodeOrNull<Line2D>("FishingLine");
        if (_fishingLine == null)
            GD.PushWarning("[MainStage] 找不到 FishingLine 节点：钓竿与鱼钩间的鱼线不显示。");

        // 相机锚点必须是 Drag Center（相机位置 = 屏幕中心），镜头跟随鱼钩时钩子才出现在画面正中。
        // 注意 Godot 4.7 C# 中枚举值与我们惯例相反：FixedTopLeft=0（相机位置=左上角）、DragCenter=1；
        // 场景文件 anchor_mode 直接写 0 会导致钩子钉在左上角。这里强制设定，不依赖场景里怎么填。
        if (_camera != null)
            _camera.AnchorMode = Camera2D.AnchorModeEnum.DragCenter;

        if (_hook == null)
            GD.PushWarning("[MainStage] 找不到 Hook 节点：抛竿/蓄力/抛出不可用。");
        else
        {
            _hook.ConnectWater(_water);
            // 鱼咬中 → 上钩（鱼进钩子，见 Hook.BeginHookUp；没挂鱼线的钩子会自动忽略）
            // 钓上 → 把鱼的放置物生成到鼠标上（见 OnHookCaught）
            _hook.Caught += OnHookCaught;
            // 一竿结束（落岸/沉底/脱钩收线回起点）→ 抛竿按钮恢复可用 + 镜头停止跟随
            _hook.Finished += OnHookFinished;
        }

        _spawner = GetNodeOrNull<FishSpawner>("FishSpawner");
        if (_spawner != null)
            _spawner.FishBitten += OnFishBitten;   // 鱼咬中 → 让钩子接上

        // 前置：仓库按钮已就位（子节点 _Ready 先于本节点）→ 把主菜单读档暂存的仓库内容铺回场景（无暂存则不动）。
        SaveManager.ApplyPendingWarehouses();

        HookWarehouseButtons();   // 场景里的仓库按钮组件：状态栏订阅其开/关/拖拽事件

        BuildUi();
        _menuUi = GetNodeOrNull<MenuUi>("UI/Hud/MenuUi/MenuPanel");
        if (_menuUi != null)
            _menuUi.Opened += CancelCharging;   // 蓄力中点右上角菜单：先取消抛竿（等同先按右键）再弹菜单
        HeldItemController.Instance.HeldChanged += OnHeldChanged;
        Localizer.LanguageChanged += OnLanguageChanged;
        OnHeldChanged(null);
    }

    public override void _ExitTree()
    {
        // autoload 的全局手跨场景存活，这里退订避免事件泄漏到已离场的本关卡。
        if (_menuUi != null) _menuUi.Opened -= CancelCharging;
        if (HeldItemController.Instance != null)
            HeldItemController.Instance.HeldChanged -= OnHeldChanged;
        Localizer.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>语言切换 → 重铺静态文字（按钮/帮助）+ 当前状态栏/挣扎/速度按新语言刷新。
    /// 图鉴页内文字由 MenuUi 自己订阅 LanguageChanged 处理；静态场景按钮由各自挂的 LocalizedText 处理。</summary>
    private void OnLanguageChanged()
    {
        if (GodotObject.IsInstanceValid(_exitFishButton)) _exitFishButton.Text = Localizer.T("ui.exit_fishing");
        if (GodotObject.IsInstanceValid(_sitFishLabel)) _sitFishLabel.Text = Localizer.T("ui.sit_fish");
        if (GodotObject.IsInstanceValid(_helpLabel)) _helpLabel.Text = Localizer.T("ui.help");
        OnHeldChanged(HeldItemController.Instance?.HeldItem);
        UpdateStruggleLabel();
        UpdateSpeedLabel();
    }

    // ---- 钓鱼：蓄力循环 0→100→0（纯函数 ChargeRatio 可单独测） ----

    public override void _Process(double delta)
    {
        if (_hook != null && IsInstanceValid(_hook))
            _hook.ReelHeld = Input.IsMouseButtonPressed(MouseButton.Left);   // 收线/施力：按住左键
        TrackFishEncounters();   // 见到判定：相机视野里的鱼 → 图鉴解锁（深度 = 生成时存的 SpawnDepth）
        UpdateSpeedLabel();
        UpdateStruggleLabel();
        StepCameraFollow(delta);
        UpdateFishingLine(delta);
        StepPlayerMovement(delta);   // 自由移动：A/D 行走 / 自动走去坐下；其余阶段静止落地
        UpdateFishSpotUi();          // 钓鱼点高亮描边 + 「坐下钓鱼」悬浮提示跟随鼠标
        // 自由移动时镜头柔和跟随玩家 X（避免走动着出屏；走回钓鱼点后镜头就停在钓鱼点附近）。
        if (Phase == GamePhase.FreeMove && _player != null && _camera != null)
            _camera.Position = _camera.Position.Lerp(
                new Vector2(_player.GlobalPosition.X, _camera.Position.Y),
                1f - Mathf.Exp(-(float)delta * 4f));
        if (!_charging) return;
        _chargeElapsed += delta;
        // 蓄力条显示 = 蓄力值在 [MinChargeValue, MaxChargeValue] 窗口内归一的 0..100：
        // 起点（< 最小值）停在 0、超出最大值停在满值 —— 最近/最远两端各有一段「停顿」。
        _powerBar.Value = ChargeToPower(ChargeRatio(_chargeElapsed) * 100.0f, MinChargeValue, MaxChargeValue) * 100.0f;
        UpdatePowerBarPosition();   // 蓄力条每帧钉在玩家节点顶端（玩家/镜头移动时跟随）
    }

    /// <summary>刷新右上角实时速度标签（水平/垂直 px/s；Hook.Velocity 是公开只读，直接读）。</summary>
    public void UpdateSpeedLabel()
    {
        if (_speedLabel == null || _hook == null || !IsInstanceValid(_hook)) return;
        var v = _hook.Velocity;
        _speedLabel.Text = Localizer.Tf("ui.speed", v.X, v.Y);
    }

    /// <summary>刷新顶部居中的上钩测试文字（体力/耐力/拉力/线长）：只在 ShowTestText 开 + 钩子上钩时显示，其余清空。</summary>
    public void UpdateStruggleLabel()
    {
        if (_struggleLabel == null) return;   // ShowTestText 关 → 不创建
        if (_hook == null || !IsInstanceValid(_hook) || _hook.Current != Hook.State.Hooked)
        {
            _struggleLabel.Text = "";
            return;
        }
        var s = _hook.Struggle;
        var line = _hook.Line;
        _struggleLabel.Text = Localizer.Tf("ui.struggle",
            s.Stamina, s.MaxStamina, s.Endurance, s.MaxEndurance,
            s.Tension, s.MaxTension,
            Localizer.T(s.Struggling ? "ui.struggle.pull" : "ui.struggle.calm"),
            _hook.LineOut, line?.Length ?? 0f);
    }

    /// <summary>
    /// 图鉴「见到」判定：每帧把「出现在相机视野里」的鱼记进解锁日志。深度用鱼生成时就存好的
    /// SpawnDepth（不测实时位置，避免追饵偏移干扰探索）。日志仅在区间实际变化时广播 → 零成本刷帧；
    /// 场上每种鱼一两条，逐帧遍历 spawner 子节点开销可忽略。
    /// </summary>
    private void TrackFishEncounters()
    {
        if (_camera == null || _spawner == null
            || !IsInstanceValid(_camera) || !IsInstanceValid(_spawner)) return;
        var viewRect = _camera.GetViewportRect();   // 相机可视矩形（画布坐标，含 zoom/锚点）
        foreach (var n in _spawner.GetChildren())
        {
            if (n is not Fish fish || fish.Def == null) continue;
            if (viewRect.HasPoint(fish.GlobalPosition))
                FishEncyclopediaLog.See(fish.Def.Id, fish.SpawnDepth);
        }
    }

    /// <summary>鱼咬中（FishSpawner 广播）→ 接上钩：让钩子把鱼带上（没挂鱼线时 BeginHookUp 自行拒绝）。</summary>
    private void OnFishBitten(Fish fish)
    {
        if (_hook == null || !IsInstanceValid(_hook)) return;
        _hook.BeginHookUp(fish);
    }

    /// <summary>钓上（Hook.Caught）：先按绑定的仓库按钮自动打开对应的仓库（见 AutoOpenWarehouseButtonPath），
    /// 再读鱼的 ItemDef 生成放置物交给全局手（放在鼠标上）。公开（public）供测试直接驱动，
    /// 与真实 Caught 事件进入的是同一条路径。</summary>
    public void OnHookCaught(Fish fish)
    {
        SetPhase(GamePhase.Ready);   // 钓上 = 一竿结束 → 退出「正在钓鱼」（此后仓库可正常开关/钓上自动开仓）
        SaveManager.NotifyCaught(fish.Def);   // 存档层记账：图鉴置位（全彩/真名）+ 累计钓获 +1
        TryAutoOpenWarehouse();   // 钓上：同时打开绑定的仓库按钮对应的仓库（仅打开，不关闭）
        var def = fish.Def?.ItemDef;
        if (def != null && HeldItemController.Instance != null)
        {
            // 格大小取当前最新打开的仓库（无则留给手回退默认）；anchorView 取当前仓库（解析初始抓手锚点用）。
            HeldItemController.Instance.TakeExternal(def.ToItem(),
                WarehouseRegistry.LastView(), WarehouseRegistry.LastView()?.CellSize);
            _status.Text = Localizer.Tf("status.caught", fish.Def?.LocalizedName ?? def.DisplayName);
        }
        else
        {
            _status.Text = Localizer.T("status.caught.noitem");
        }
    }

    /// <summary>刷新钓竿与鱼钩间的鱼线（Line2D，6px 连续细线）：一端 = 抛竿起点（钓竿，Hook.CastOrigin），另一端 = 鱼钩当前位置。
    /// 钩子下落时线略微上拱（鱼钩落得快、鱼线中段跟不上），收线/停住/空闲时迅速转为笔直。
    /// 抛竿后每帧由 _Process 调用（FishingLine 节点放世界原点，Points 即世界坐标）。</summary>
    public void UpdateFishingLine(double delta)
    {
        if (_fishingLine == null || _hook == null || !IsInstanceValid(_hook)) return;
        var taut = !_hook.IsBusy || _hook.Current is Hook.State.Reeling or Hook.State.Hooked;   // 收线/上钩（挣扎/拖回/收回）：线绷直
        var dist = (_hook.GlobalPosition - _hook.CastOrigin).Length();
        var target = Mathf.Min(FishingLineCurve.TargetSag(_hook.Velocity.Y, taut, LineMaxSag, LineSagFallSpeed), dist * 0.25f);   // 短线不画夸张大拱
        _lineSag = Mathf.MoveToward(_lineSag, target, LineSagResponse * (float)delta);
        _fishingLine.Points = FishingLineCurve.Samples(_hook.CastOrigin, _hook.GlobalPosition, _lineSag, LineSagPeak);
        _fishingLine.Visible = _hook.IsBusy;   // 飞行/下沉/收线（含停住待收）时显示，空闲与一竿结束隐藏
        _fishingLine.DefaultColor = LineTensionColor(_hook.Struggle.Tension, LineTensionRedFrom, LineTensionRedAt);   // 拉力越高线越红（濒临断线预警）
    }

    /// <summary>拉力 → 鱼线颜色（纯函数，测试可单测）：拉力 &lt; from → 纯白（不预警）；
    /// from..at 之间线性由白渐变到红；≥ at → 全红。at ≤ from 退化回纯白（不启用预警）。</summary>
    public static Color LineTensionColor(float tension, float from, float at)
    {
        if (at <= from) return Colors.White;
        var t = Mathf.Clamp((tension - from) / (at - from), 0f, 1f);
        return Colors.White.Lerp(Colors.Red, t);
    }

    /// <summary>蓄力条的三角形波形：每 2 秒一个循环，0s→0，1s→100，2s→0，循环往复（纯函数，测试友好）。</summary>
    public static float ChargeRatio(double elapsedSeconds)
    {
        var cycle = elapsedSeconds % 2.0;
        var ratio = cycle <= 1.0 ? cycle : 2.0 - cycle;
        return (float)ratio;
    }

    /// <summary>蓄力值（0..100）→ 归一化抛投力度（0..1）：
    ///  ▪ ≤ MinChargeValue → 0（最近）；≥ MaxChargeValue → 1（最远）；中间在窗口内线性映射；
    ///  ▪ 两个阈值自动对调（min &gt; max → 交换，例填 80/60 按 60/80 用）；
    ///  ▪ 两者相等（退化配置）回退旧 0..100 直连（obs/障碍防御，避免除零）。
    /// 蓄力条显示与抛投力度共用本函数 —— 玩家看到的条「0 停 → 满值停」也就是实际抛出的那一档力。</summary>
    public static float ChargeToPower(float charge, float minCharge, float maxCharge)
    {
        var lo = Mathf.Min(minCharge, maxCharge);
        var hi = Mathf.Max(minCharge, maxCharge);
        var span = hi - lo;
        if (span <= 1e-4f) return Mathf.Clamp(charge, 0f, 100f) / 100f;   // min==max：退化直连
        return Mathf.Clamp((charge - lo) / span, 0f, 1f);
    }

    /// <summary>空手 + 鱼钩空闲时点击左键 = 抛竿（开始蓄力）：钓竿力度进度条出现在玩家节点顶端，
    /// 再点一次左键即抛出。取代原来的钓竿按钮——鼠标上没有放置物、鱼钩未被抛出时，左键即视为抛竿。</summary>
    private void StartCharging()
    {
        if (_hook == null || !IsInstanceValid(_hook)) return;
        if (_charging || _hook.IsBusy) return;   // 已在蓄力 / 钩子忙（飞行下沉收线上钩）→ 无效
        SetPhase(GamePhase.Fishing);   // 开始准备抛竿 → 进入「正在钓鱼」：仓库按钮被锁定直到钓上/一竿结束
        // 抛竿前记录镜头初始位置（一竿结束后的回归目标）；镜头若正处在上一次的回归动画中则保留原目标不动。
        if (!_cameraReturning && _camera != null) _cameraRestPos = _camera.Position;
        _charging = true;
        _chargeElapsed = 0.0;
        _powerBar.Value = 0.0;
        RefreshButtons();
        UpdatePowerBarPosition();   // 出现的第一帧就钉到玩家顶端（不必等 _Process）
    }

    /// <summary>取消抛竿（蓄力中右键触发 / 蓄力中点右上角菜单也先走它；也可直接调用）：关闭蓄力条、蓄力值复位，
    /// 退出「正在钓鱼」退回 Ready —— StartCharging 进入 Fishing 的对称反向路径（状态机）。空手时调用无害。</summary>
    public void CancelCharging()
    {
        if (!_charging) return;
        SetPhase(GamePhase.Ready);
        _charging = false;
        _chargeElapsed = 0.0;
        _powerBar.Visible = false;
        RefreshButtons();
    }

    /// <summary>抛出：按当前蓄力值把钩子以抛物线抛出，结束蓄力、蓄力条隐藏，镜头开始跟随鱼钩。</summary>
    private void ThrowCast()
    {
        if (_hook == null || !IsInstanceValid(_hook)) return;
        var power01 = (float)(_powerBar.Value / _powerBar.MaxValue);
        _charging = false;
        _hook.Cast(power01);
        StartCameraFollow();
        RefreshButtons();
    }

    /// <summary>蓄力条位置（Hud 本地坐标）：玩家节点顶部正中心（玩家 Sprite2D 局部纹理矩形的顶边中点），
    /// 经世界 →（画布变换）→ 屏幕 →（Hud 全局矩阵逆）→ 本地 换算；与仓库按钮组件的锚点换算（ResolveCenterHudLocal）同款。
    /// Hud 缺省/玩家缺失时停在初始位置。</summary>
    private void UpdatePowerBarPosition()
    {
        if (!_powerBar.Visible) return;
        if (_hud == null || _playerSprite == null || !IsInstanceValid(_playerSprite) || _playerSprite.Texture == null) return;
        var rect = _playerSprite.GetRect();   // 局部纹理矩形（centered 时以纹理中心为原点，配合 scale/旋转）
        var topLeft = _playerSprite.ToGlobal(rect.Position);
        var topRight = _playerSprite.ToGlobal(rect.Position + new Vector2(rect.Size.X, 0f));
        var topCenter = (topLeft + topRight) / 2f;
        var screen = GetViewport().GetCanvasTransform() * topCenter;
        var local = _hud.GetGlobalTransformWithCanvas().AffineInverse() * screen;
        var bar = _powerBar.CustomMinimumSize;
        _powerBar.Position = new Vector2(local.X - bar.X / 2f, local.Y - bar.Y - 6f);   // 顶在玩家上方
    }

    // ---- 镜头跟随 ----

    /// <summary>抛出后开始跟随：记录起点，未来 CameraFollowDuration 秒内 easeOut 平滑居中，随后持续钉住鱼钩。
    /// 若此前镜头正处在上一次一竿结束后的回归动画中，跟随直接接管（从当前位置平滑出发，回归取消）。</summary>
    private void StartCameraFollow()
    {
        if (_camera == null) return;
        _cameraReturning = false;   // 新一竿的跟随接管镜头
        _cameraFrom = _camera.Position;
        _cameraElapsed = 0.0;
        _cameraFollowing = true;
    }

    /// <summary>
    /// 推进镜头（每帧由 _Process 调用；测试也可手动步进，见 TestFishing）。
    ///  ▪ 回归中（一竿结束）：前 CameraFollowDuration(1) 秒内从「一竿结束时的位置」easeOutCubic 平滑
    ///    回到抛竿前记录的初始位置 _cameraRestPos，到位后停止——再次抛竿的跟随会覆盖接管；
    ///  ▪ 跟随中（抛竿后）：前 CameraFollowDuration(1) 秒内从起点 easeOutCubic 插值到鱼钩位置——鱼钩 1 秒内
    ///    逐渐居中；之后每帧直接钉在鱼钩当前位置（飞行/入水/收线/脱钩收回 全程持续跟随，到一竿结束再回归）。
    /// </summary>
    public void StepCameraFollow(double delta)
    {
        if (_camera == null) return;

        if (_cameraReturning)
        {
            _cameraReturnElapsed += delta;
            var t = Mathf.Min((float)(_cameraReturnElapsed / CameraFollowDuration), 1f);
            var eased = 1f - Mathf.Pow(1f - t, 3f);   // easeOutCubic：先快后慢，1 秒回到初始位置
            _camera.Position = _cameraReturnFrom.Lerp(_cameraRestPos, eased);
            if (t >= 1f) _cameraReturning = false;
            return;
        }

        if (!_cameraFollowing || _hook == null || !IsInstanceValid(_hook))
        {
            _cameraFollowing = false;
            return;
        }

        _cameraElapsed += delta;
        var ft = Mathf.Min((float)(_cameraElapsed / CameraFollowDuration), 1f);
        var feased = 1f - Mathf.Pow(1f - ft, 3f);   // easeOutCubic：先快后慢，1 秒到中央
        _camera.Position = _cameraFrom.Lerp(_hook.GlobalPosition, feased);
    }

    /// <summary>一竿真正结束（收线收完回起点/落岸兜底）：退出「正在钓鱼」（未钓上的一竿也要解锁仓库），
    /// 蓄力复位、蓄力条隐藏、镜头停止跟随鱼钩，并从当前位置 1 秒内平缓回到抛竿前记录的初始位置
    /// （钓到鱼或断线收回到家后，可再次空手左键抛竿）。公开（public）供测试直接驱动阶段退出。
    /// Finished 在每次一竿结束都会触发（含钓上后的 CatchFish → FinishRest，此时阶段已由 OnHookCaught 先退回）。</summary>
    public void OnHookFinished()
    {
        SetPhase(GamePhase.Ready);   // 一竿结束（无论钓上与否）→ 退出「正在钓鱼」
        _cameraFollowing = false;
        if (_camera != null)
        {
            // 开始回归：从「一竿结束时镜头所在」1 秒内平缓回到抛竿前记录的初始位置
            _cameraReturning = true;
            _cameraReturnFrom = _camera.Position;
            _cameraReturnElapsed = 0.0;
        }
        _charging = false;
        _powerBar.Visible = false;
        RefreshButtons();
    }

    private void BuildUi()
    {
        // 界面控件一律挂到 UI 层下的 Hud（CanvasLayer 不受镜头影响，UI 固定屏幕）；Hud 缺失兜底挂世界根。
        Node ui = _hud as Node ?? this;

        // 「退出钓鱼」：仅 Ready 阶段显示（见 RefreshButtons）；点击切到自由移动。
        // 「获取放置物 / 丢弃放置物」已移到独立关卡 InventoryDemo。
        _exitFishButton = new Button { Name = "ExitFishingButton", Text = Localizer.T("ui.exit_fishing") };
        _exitFishButton.AddThemeFontSizeOverride("font_size", 20);
        _exitFishButton.Pressed += ExitToFreeMove;
        ui.AddChild(_exitFishButton);

        _powerBar = new ProgressBar
        {
            Name = "PowerBar",
            Position = new Vector2(16, 100),   // 兜底初始位；蓄力开始后由 UpdatePowerBarPosition 钉到玩家顶端
            CustomMinimumSize = new Vector2(180, 18),
            MinValue = 0.0,
            MaxValue = 100.0,
            Value = 0.0,
            Visible = false,                    // 进入蓄力才出现
            MouseFilter = Control.MouseFilterEnum.Ignore,   // 纯显示：抛竿靠左键，不能让它吞点击
        };
        ui.AddChild(_powerBar);

        _status = new Label { Name = "Status", Position = new Vector2(16, 60), Text = "" };
        ui.AddChild(_status);

        // 「坐下钓鱼」悬浮提示：自由移动 + 鼠标悬停在钓鱼点上时显示，跟随鼠标。
        _sitFishLabel = new Label
        {
            Name = "SitFishLabel",
            Text = Localizer.T("ui.sit_fish"),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,   // 纯提示，不吃点击
        };
        _sitFishLabel.AddThemeColorOverride("font_color", new Color(1f, 0.97f, 0.85f));
        _sitFishLabel.AddThemeFontSizeOverride("font_size", 26);
        ui.AddChild(_sitFishLabel);

        _helpLabel = new Label
        {
            Name = "HelpLabel",
            Position = new Vector2(16, 1030),
            Text = Localizer.T("ui.help"),
        };
        ui.AddChild(_helpLabel);

        // 右上角实时速度：锚 TopRight、文本右对齐贴着右上角（水平/垂直 px/s，每帧由 UpdateSpeedLabel 刷新）
        _speedLabel = new Label
        {
            Name = "SpeedLabel",
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "",
        };
        _speedLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _speedLabel.OffsetLeft = -260f;
        _speedLabel.OffsetTop = 10f;
        _speedLabel.OffsetRight = -10f;
        _speedLabel.OffsetBottom = 34f;
        ui.AddChild(_speedLabel);
        UpdateSpeedLabel();

        // 顶部居中上钩测试文字（体力/耐力/拉力/线长）：ShowTestText 一键开关，false 不创建
        if (ShowTestText)
        {
            _struggleLabel = new Label
            {
                Name = "StruggleLabel",
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,   // 内容在框内居中
            };
            _struggleLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _struggleLabel.OffsetLeft = -500f;
            _struggleLabel.OffsetRight = 500f;
            _struggleLabel.OffsetTop = 10f;
            _struggleLabel.OffsetBottom = 34f;
            ui.AddChild(_struggleLabel);
        }

        RefreshButtons();
    }

    /// <summary>按钮显隐/禁用刷新：「退出钓鱼」= 仅 Ready 阶段；蓄力条 = 仅蓄力时出现（位置由
    /// UpdatePowerBarPosition 每帧钉到玩家顶端）。钓鱼本身没有按钮了——空手左键抛竿/再点抛出。</summary>
    private void RefreshButtons()
    {
        _exitFishButton.Visible = Phase == GamePhase.Ready;
        _powerBar.Visible = _charging;
    }

    // ---- 输入：点击仓库按钮（宝箱）开关各自的仓库 ----

    /// <summary>
    /// 左键点击路由（UI 按钮/仓库视图是 Stop 已先消费，这里只兜底空白区域与世界对象）：
    ///  1) 命中某个仓库按钮（挂 WarehouseButton 组件的 Sprite2D）→ 切换那按钮自己的仓库（最高优先，始终生效——
    ///    多仓库打开时各按钮仍是「隐藏所对应仓库」的合法入口）；
    ///  2) 仓库打开中（未命中任何仓库按钮/仓库页 = 点击落在仓库页面以外；仓库页的点击已被视图自身消费，到不了这里）：
    ///        单仓库 + 空手 → 关闭当前打开的仓库（防止空手时误触发抛竿等世界互动）；
    ///        单仓库 + 手持放置物 → 不关闭（放/拾由仓库页消费，这里兜底不抛竿）；
    ///        多仓库同时打开（≥2）→ 外部点击一律不生效（禁止与其他物品互动）；
    ///  3) 手持放置物 → 左键只用于放/拾（由仓库视图处理；这里直接忽略，不触发抛竿）；
    ///  4) 鱼钩正在落水下沉 → 进入收线模式（一记点击锁定线长，后续按住由 _Process 的 ReelHeld 持续写入）；
    ///  5) 鱼钩忙（飞行/收线/上钩）→ 忽略；
    ///  6) 蓄力中 → 抛出；空闲 → 抛竿（开始蓄力）。
    /// 注：空手点仓库背景/空格子按住拖拽整仓的事件在仓库视图内被 GUI 捕获消费，不会到这里（不误关仓/不误抛竿）。
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb) return;
        if (!mb.Pressed) return;

        // 右键：蓄力（抛竿进度条出现）中 = 取消抛竿 —— 关闭蓄力条、退出「正在钓鱼」退回 Ready（CancelCharging）。
        // 非蓄力时右键没有其它语义，保持原样（不消费、不进入后续逻辑；拿起/旋转右键由仓库视图 GUI 先处理）。
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (_charging)
            {
                GetViewport().SetInputAsHandled();
                CancelCharging();
            }
            return;
        }
        if (mb.ButtonIndex != MouseButton.Left) return;

        var canvasPoint = GetViewport().GetCanvasTransform().AffineInverse() * mb.Position;
        if (WarehouseButton.HitTest(canvasPoint) is { } btn)
        {
            GetViewport().SetInputAsHandled();
            // 正在钓鱼（蓄力开始 → 钓上/一竿结束）：点击仓库按钮被吞掉，开关仓库全部禁止
            // （否则钓鱼过程中误点宝箱会弹出仓库，打断操作）。一竿结束回到 Ready 后恢复。
            if (Phase == GamePhase.Fishing) return;
            btn.Toggle();   // 各按钮开/关它自己配置的那座仓库
            return;
        }

        // 仓库模态：点击落在仓库页面以外（未命中任何仓库按钮/仓库页）。单仓库 + 空手 → 关闭当前仓库；
        // 多仓库同时打开（≥2）→ 外部点击一律不生效，仅各自的仓库按钮仍可隐藏所对应的仓库。
        var openCount = WarehouseRegistry.OpenCount;
        if (openCount > 0)
        {
            GetViewport().SetInputAsHandled();   // 仓库已打开：外界点击不再落入钓鱼/世界逻辑
            if (openCount > 1) return;
            if (HeldItemController.Instance?.HeldItem != null) return;   // 手持放置物：不关闭仓库也不抛竿
            CloseCurrentOpened();
            return;
        }

        // 手持放置物时左键是「放/拾」语义（落在仓库格上已被视图消费，这里兜底世界区）：不抛竿。
        if (HeldItemController.Instance?.HeldItem != null) return;

        // 自由移动（FreeMove）：不抛竿/不收线。左键点到钓鱼点检测范围 = 走过去坐下；其它点击不吃。
        if (Phase == GamePhase.FreeMove)
        {
            if (_fishSpot != null && _fishSpot.IsPointInside(canvasPoint))
                StartWalkingToSpot();
            return;
        }

        if (_hook == null || !IsInstanceValid(_hook)) return;
        if (_hook.Current == Hook.State.Sinking) { _hook.BeginReel(); return; }   // 落水后：收线
        if (_hook.IsBusy) return;                                                 // 飞行/收线/上钩中：点击无效
        if (_charging) { ThrowCast(); return; }                                   // 蓄力中：再点即抛出
        if (Phase != GamePhase.Ready) return;                                     // 防御：只允许 Ready 抛竿
        StartCharging();                                                          // 空闲：左键即抛竿
    }

    /// <summary>订阅场景里所有仓库按钮的生命周期/拖拽事件 → 刷新状态栏（组件不碰宿主 UI）。
    /// 运行期新加入的按钮也能用（开关逻辑在按钮自身，只是没有这里的状态栏提示）。</summary>
    private void HookWarehouseButtons()
    {
        foreach (var n in FindChildren("*", recursive: true, owned: false))
        {
            if (n is not WarehouseButton b) continue;
            b.Opened += () =>
            {
                if (GodotObject.IsInstanceValid(b) && GodotObject.IsInstanceValid(_status) && b.StorageDef != null)
                    _status.Text = Localizer.Tf("status.warehouse.opened", b.StorageDef?.DisplayName ?? "");
            };
            b.Closed += () =>
            {
                if (GodotObject.IsInstanceValid(_status))
                    _status.Text = Localizer.T("status.warehouse.closed");
            };
            b.Moved += () =>
            {
                if (GodotObject.IsInstanceValid(_status))
                    _status.Text = Localizer.T("status.warehouse.moved");
            };
        }
    }

    /// <summary>单仓库打开 + 空手时点击仓库页面以外 → 关闭当前打开的仓库（隐藏、数据保留，
    /// 与按钮隐藏同一生命周期）。经登记表定位「唯一打开中的视图」再关它所属的按钮；无按钮归属时退化为直接隐藏。</summary>
    private void CloseCurrentOpened()
    {
        var view = WarehouseRegistry.SingleOpen();
        if (view == null) return;
        if (view.OwnerButton is WarehouseButton btn) btn.Close();
        else view.Hide();
        _status.Text = Localizer.T("status.warehouse.close");
    }

    /// <summary>钓上时自动打开绑定的仓库按钮：路径有效且能解析到 WarehouseButton → 打开那座仓库
    /// （仅打开、不关闭；已打开保持不变；位置 = 上次拖拽/打开的位置）。未配置/空路径/节点无效 = 不自动打开（保持旧行为）。</summary>
    private void TryAutoOpenWarehouse()
    {
        var btn = ResolveAutoOpenButton();
        if (btn != null && IsInstanceValid(btn)) btn.Open();
    }

    /// <summary>解析钓上自动打开的目标按钮（懒解析：运行期改 AutoOpenWarehouseButtonPath 即刻生效，
    /// 测试可据此切换路径验证开关行为）。</summary>
    private WarehouseButton? ResolveAutoOpenButton()
    {
        if (AutoOpenWarehouseButtonPath == null) return null;
        if (string.IsNullOrEmpty(AutoOpenWarehouseButtonPath.ToString())) return null;   // 空路径 = 关闭自动打开
        var btn = GetNodeOrNull<WarehouseButton>(AutoOpenWarehouseButtonPath);
        if (btn == null && !_warnedAutoOpenMissing)
        {
            _warnedAutoOpenMissing = true;
            GD.PushWarning($"[MainStage] 钓上自动打开仓库的按钮路径无效：{AutoOpenWarehouseButtonPath}"
                           + "（钓上不自动打开仓库；请把该导出改为场景里已挂 WarehouseButton 的节点路径）");
        }
        return btn;
    }

    // ---- 自由移动：退出钓鱼 / 玩家移动 / 钓鱼点交互（获取/丢弃已移到 InventoryDemo 场景） ----

    /// <summary>「退出钓鱼」（仅 Ready 阶段显示）：切到自由移动（A/D 左右走），走回钓鱼点点击坐下。</summary>
    public void ExitToFreeMove()
    {
        if (Phase != GamePhase.Ready) return;
        SetPhase(GamePhase.FreeMove);
        _status.Text = Localizer.T("status.freemove");
        RefreshButtons();
    }

    /// <summary>每帧玩家移动：FreeMove 时 A/D 左右走 / 自动走到钓鱼点（到位切 Ready）；其余阶段静止。
    /// CharacterBody2D 带下落重力，踩在场景 Ground 上站稳。暴露给测试可注入 delta 直接步进。</summary>
    public void StepPlayerMovement(double delta)
    {
        if (_player == null || !IsInstanceValid(_player)) return;

        // 自动走到钓鱼点：直接位移（确定性，测试友好；不做物理，避免被地面碰撞干扰走向）。
        if (Phase == GamePhase.FreeMove && _walkingToSpot)
        {
            var dx = _walkTargetX - _player.GlobalPosition.X;
            var step = Mathf.Clamp(Mathf.Sign(dx) * WalkSpeed * (float)delta, -Mathf.Abs(dx), Mathf.Abs(dx));
            _player.GlobalPosition += new Vector2(step, 0f);
            if (Mathf.Abs(dx) <= 3f)
            {
                _player.GlobalPosition = new Vector2(_walkTargetX, _player.GlobalPosition.Y);
                _player.Velocity = Vector2.Zero;
                _walkingToSpot = false;
                SetPhase(GamePhase.Ready);   // 走到钓鱼点 → 「准备钓鱼」
                _status.Text = Localizer.T("status.ready_fish");
                RefreshButtons();
            }
            return;
        }

        // FreeMove 的 A/D 左右走 / 其它阶段静止：CharacterBody2D 带重力，踩在地面 Ground 上站稳。
        var moveX = 0f;
        if (Phase == GamePhase.FreeMove)
        {
            if (Input.IsPhysicalKeyPressed(Key.A)) moveX -= WalkSpeed;
            if (Input.IsPhysicalKeyPressed(Key.D)) moveX += WalkSpeed;
        }
        var vel = _player.Velocity;
        vel.X = moveX;
        vel.Y += WalkGravity * (float)delta;
        _player.Velocity = vel;
        _player.MoveAndSlide();
    }

    /// <summary>钓鱼点 UI（每帧调用，仅 FreeMove 有意义）：
    ///  ▪ 玩家在检测范围内 → 钓鱼点高亮描边（SetHighlight）；
    ///  ▪ 鼠标悬停在检测范围内 → 「坐下钓鱼」悬浮提示显示并跟随鼠标（mouseWorld 可注入供测试）。</summary>
    public void UpdateFishSpotUi(Vector2? mouseWorld = null)
    {
        var spot = _fishSpot;
        if (spot == null) return;

        var hovering = spot.IsPointInside(mouseWorld ?? GetGlobalMouseWorld());
        if (Phase == GamePhase.FreeMove)
        {
            spot.SetHighlight(_player != null && spot.IsPointInside(_player.GlobalPosition));
            _sitFishLabel.Visible = hovering;
            if (hovering && _hud != null) FollowMouseInHud(mouseWorld ?? GetGlobalMouseWorld());
        }
        else
        {
            spot.SetHighlight(false);
            _sitFishLabel.Visible = false;
        }
    }

    /// <summary>点击钓鱼点：让小玩家以行走速度自动走到钓鱼点 X，到位后切 Ready（抛竿可用）。</summary>
    public void StartWalkingToSpot()
    {
        if (_fishSpot == null || _player == null) return;
        _walkingToSpot = true;
        _walkTargetX = _fishSpot.GlobalPosition.X;
        _status.Text = Localizer.T("status.sit_fish_goto");
    }

    /// <summary>把世界坐标换算成 Hud 本地坐标并让悬浮提示跟到鼠标旁。</summary>
    private void FollowMouseInHud(Vector2 mouseWorld)
    {
        if (_hud == null) return;
        var screen = GetViewport().GetCanvasTransform() * mouseWorld;
        var local = _hud.GetGlobalTransformWithCanvas().AffineInverse() * screen;
        _sitFishLabel.Position = local + new Vector2(16, 16);
    }

    /// <summary>当前全局鼠标世界坐标（画布坐标；与 HeldItemController.GlobalMouse 同做法）。</summary>
    private Vector2 GetGlobalMouseWorld()
    {
        var view = WarehouseRegistry.LastView();
        return view != null ? view.GetGlobalMousePosition() : GetViewport().GetMousePosition();
    }

    /// <summary>全局手状态变化（拿起/放下/钓上物品/隐藏还原）→ 刷新按钮与状态栏。</summary>
    private void OnHeldChanged(InventoryItem? held)
    {
        RefreshButtons();
        if (held == null)
        {
            _status.Text = Localizer.T("status.hand.empty");
            return;
        }
        var name = (held.Data as PlacementItemTypeDef)?.DisplayName ?? "未知物品";
        _status.Text = Localizer.Tf("status.holding", name,
            held.Shape.Height, held.Shape.Width, held.Shape.CellCount);
    }
}
using Godot;
using SaleFish.Fishing;

namespace SaleFish.Main;

/// <summary>
/// 鱼挣扎测试演示关卡（Scenes/FishStruggleDemo.tscn）：不依赖整条钓鱼流程，直接验证一条鱼的
/// 挣扎形态——拖动镜头 + 播放 FishDef「挣扎效果」分类配置的特效/震屏 + 切换动画播放速度。
///
/// 用法（检查器配置根节点）：
///  ▪ FishDefConfig —— 放一份鱼定义 .tres：把它配置的动画精灵表（SpriteFrames）、挣扎特效
///    （StruggleFxPrefab）与震屏（StruggleScreenShake）都用上；null = 不生成鱼，按钮无效果。
///  ▪ MoveSpeed —— 开始挣扎后镜头带着鱼向右平移的速度（px/s），可当场调手感。
///
/// 行为：
///  ▪ 配置了 FishDefConfig 时，在原本 Player 的位置（本场景坐标原点）生成这条鱼的精灵
///    （实例化 FishPrefab 注入 Def：朝向右侧 = Scale.X 为负、动画保持 NormalPlaySpeed）。
///  ▪ 「开始挣扎」：在鱼位置播一次 FishDef 上的特效 + 震屏（走公共 StruggleFeedback.Play，
///    与真实挣扎代码同一套，无钩子也能用），动画切到 StrugglePlaySpeed，之后每帧鱼与镜头
///    一起以 MoveSpeed 向右移动（模拟吃力拖线）。
///  ▪ 「停止挣扎」：动画切回 NormalPlaySpeed，鱼与镜头都回到原点。
///
/// 动画切档复用 Fish.ApplyPlayback（internal，同程序集可调）：Normal/Struggle/Exhausted 三档
/// SpeedScale 读 FishDef，与真实挣扎完全一致，不会两套逻辑漂移。
/// </summary>
public sealed partial class FishStruggleDemo : Node2D
{
    /// <summary>要拿来测试的鱼定义（精灵表 + 挣扎特效/震屏 + 播放速度全从它读）。null = 不生成鱼。</summary>
    [Export] public FishDef? FishDefConfig;

    /// <summary>鱼的 prefab（根挂 Fish.cs，带 AnimatedSprite2D + CollisionShape2D）；留空回退代码 new Fish()（无动画纯逻辑）。</summary>
    [Export] public PackedScene? FishPrefab;

    /// <summary>开始挣扎后镜头 + 鱼一起向右移动的速度（px/s）。</summary>
    [Export] public float MoveSpeed = 120f;

    private Fish? _fish;
    private Camera2D? _camera;
    private Label _status = null!;
    private bool _struggling;

    /// <summary>当前演示鱼（internal：测试可用同一程序集断言；null = 未配置 FishDef）。</summary>
    internal Fish? Fish => _fish;

    /// <summary>当前是否在挣扎（internal：测试断言用）。</summary>
    internal bool Struggling => _struggling;

    public override void _Ready()
    {
        GetWindow().Mode = Window.ModeEnum.Maximized;   // 与主关卡同一显示方式：全分辨率展示统一视野
        _camera = GetNodeOrNull<Camera2D>("Camera2D");

        if (FishDefConfig != null) SpawnFish(FishDefConfig);
        else GD.PushWarning("[FishStruggleDemo] 未配置 FishDefConfig：不会生成鱼，两个按钮将无效果。");

        BuildUi();
    }

    public override void _Process(double delta)
    {
        if (_struggling && _fish != null && _camera != null)
        {
            // 鱼向右平移（模拟挣扎拖线），镜头钉在鱼身上跟着动
            var step = MoveSpeed * (float)delta;
            _fish.GlobalPosition += Vector2.Right * step;
            _camera.GlobalPosition = _fish.GlobalPosition;
        }

        if (_status != null)
        {
            var name = FishDefConfig?.DisplayName ?? "（未配置）";
            _status.Text = _struggling
                ? $"挣扎中：{name} · 向右移动 {MoveSpeed:0} px/s"
                : $"就绪：{name} · 点「开始挣扎」播放特效+震屏并拖动";
        }
    }

    // ---- 世界参考系绘制（画在 Node2D 根 = 世界坐标，原点静止；鱼/镜头动它不动，直接看得出归位与否）----
    // 视觉参考：网格 + 原点「瞄准环」+ 表盘刻度 + 中心十字。起点退化等一概不影响逻辑；只是肉眼对比用。

    /// <summary>
    /// 画原点静止基准环（原 ScreenShakeDemo 的参考系，保留给挣扎归位对比用）：网格 + 半径 260 的圆环
    /// （瞄准环）+ 12 格表盘刻度 + 中心十字。Node2D 根节点定位在世界原点，鱼被拖走时环留在原地，
    /// 点「停止挣扎」后鱼回原点、正好落进环里 —— 一眼确认归位是否正确。
    /// </summary>
    public override void _Draw()
    {
        const float span = 1500f;
        const float step = 100f;
        for (var x = -span; x <= span; x += step)
            DrawLine(new Vector2(x, -span), new Vector2(x, span), new Color(1, 1, 1, 0.07f), 1f);
        for (var y = -span; y <= span; y += step)
            DrawLine(new Vector2(-span, y), new Vector2(span, y), new Color(1, 1, 1, 0.07f), 1f);

        const float ring = 260f;              // 瞄准环：半径 260，鱼停在环心 = 已归位
        DrawArc(new Vector2(0, 0), ring, 0f, Mathf.Tau, 128, new Color(1, 1, 1, 0.3f), 2f);
        for (var i = 0; i < 12; i++)
        {
            var a = Mathf.Tau * i / 12f;
            var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (ring + 60f);
            DrawRect(new Rect2(p - new Vector2(5, 5), new Vector2(10, 10)), new Color(1, 1, 1, 0.28f));
        }

        DrawLine(new Vector2(-140, 0), new Vector2(140, 0), new Color(1, 1, 1, 0.35f), 2f);
        DrawLine(new Vector2(0, -140), new Vector2(0, 140), new Color(1, 1, 1, 0.35f), 2f);
    }

    // ---- 生成演示鱼（复用 FishPrefab + Fish.cs 的真实精灵/朝向/动画逻辑） ----

    private void SpawnFish(FishDef def)
    {
        _fish = FishPrefab != null ? FishPrefab.Instantiate<Fish>() : new Fish();
        _fish.Def = def;                     // 精灵表 + 动画三档播放速度 + 挣扎特效/震屏数据都从它读
        _fish.BehaviorStrategy = null;       // 演示鱼不自主动；只有本脚本拖着它走
        _fish.SpawnAtReady = false;          // 不按行为数据乱刷位置：钉在本脚本出生点
        _fish.BehaviorData = FishBehaviorData.Unconfigured;   // 无巡逻矩形（Unconfigured 兜底）
        _fish.GlobalPosition = Vector2.Zero; // 原本 Player 的位置（本场景坐标原点）
        AddChild(_fish);
        _fish.SetFacingLeft(false);          // 朝向右侧 = Scale.X 负（镜像）→ 鱼头朝右
    }

    // ---- UI：两个按钮 + 状态标签（全部代码构建，Scene 文件保持最小） ----

    private void BuildUi()
    {
        var ui = GetNodeOrNull<CanvasLayer>("UI");
        if (ui == null) { GD.PushWarning("[FishStruggleDemo] 找不到 UI：界面将挂回世界根。"); ui = new CanvasLayer { Name = "UI" }; AddChild(ui); }

        var title = new Label
        {
            Text = "鱼挣扎测试：开始挣扎→特效+震屏+向右拖动；停止挣扎→归位",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        title.OffsetLeft = -500f;
        title.OffsetRight = 500f;
        title.OffsetTop = 14f;
        title.OffsetBottom = 40f;
        ui.AddChild(title);

        var bar = new HBoxContainer { Position = new Vector2(16, 60) };
        bar.AddThemeConstantOverride("separation", 10);
        ui.AddChild(bar);

        var start = new Button { Text = "开始挣扎" };
        start.Pressed += StartStruggle;
        bar.AddChild(start);

        var stop = new Button { Text = "停止挣扎" };
        stop.Pressed += StopStruggle;
        bar.AddChild(stop);

        _status = new Label { Position = new Vector2(16, 96), Text = "" };
        ui.AddChild(_status);

        var help = new Label
        {
            Position = new Vector2(16, 1020),
            Text = "用法：给 FishDefConfig 挂一份鱼定义（自带精灵表+挣扎特效/震屏），点「开始挣扎」会在鱼位置各播一次特效与震屏（走与游戏相同的 StruggleFeedback 公共播放器，无需钩子），并让鱼以 MoveSpeed 向右拖动；「停止挣扎」动画回 NormalPlaySpeed、鱼和镜头归位 · 原点有静止「瞄准环」参考系：鱼被拖走时环留在原地，归位后鱼正好落回环心",
        };
        ui.AddChild(help);
    }

    /// <summary>触发「开始挣扎」：播一次特效+震屏、动画切 StrugglePlaySpeed（internal：按钮与测试共用同一入口）。</summary>
    internal void StartStruggle()
    {
        if (_fish == null) { _status.Text = "未配置 FishDef：没有鱼可挣扎。"; return; }

        _struggling = true;
        // 播一次 FishDef 上配置的特效 + 震屏（公共播放器，无钩子可用；锚点 = 鱼当前位置）
        StruggleFeedback.Play(_fish.Def, this, _fish.GlobalPosition);
        // 动画切到挣扎档
        _fish.ApplyPlayback(FishPlayback.Struggle);
    }

    /// <summary>触发「停止挣扎」：动画回 NormalPlaySpeed、鱼与镜头都回到原点（internal：按钮与测试共用同一入口）。</summary>
    internal void StopStruggle()
    {
        _struggling = false;
        if (_fish != null)
        {
            _fish.ApplyPlayback(FishPlayback.Normal);          // 动画回正常档
            _fish.GlobalPosition = Vector2.Zero;               // 鱼回原点
        }
        if (_camera != null) _camera.GlobalPosition = Vector2.Zero;   // 镜头回原点
    }
}
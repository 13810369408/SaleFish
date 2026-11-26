using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// 鱼种策略槽测试（FishDef.BehaviorStrategy / FishDef.Struggle 两个策略槽 + 抽象基类 + 上下文对象）：
/// 验证「在 resource 里选策略」的机制（Godot 版 Odin + [SerializeReference]）。四部分：
///  ▪ A. 内置策略：挂着 ClassicBehavior（数据就是策略自己的导出参数）的鱼照常在巡逻矩形内游
///    （回归内置状态机搬家到策略后的路径，数据经 ctx.BehaviorData 从策略参数流动）；
///  ▪ B. 行为完全接管：自定义策略每帧按自定义直线逻辑移动，内置巡逻状态机不再生效
///    （且能游出默认区块钳制范围——内置策略绝不允许），上下文原语（MoveToward/SetState 等）可用；
///  ▪ C. 上钩挣扎完全接管：自定义挣扎策略 OnHooked + Update 的鱼，钩子按自定义 PullVelocity 移动、
///    数值机完全不被构建/触碰；主动 ReleaseRequested → 走脱钩清理链路（鱼消失、装备清空、收线结束）；
///  ▪ D. 空槽语义：Behavior 空 = 鱼不动只漂着（生成警告）；Struggle 空 = 上钩后安静待收线（上钩警告），
///    按住左键可直接收线钓上，整条链路可走。
///
/// 钩子/鱼 ProcessMode 全部 Disabled + 手动 Step(dt) 驱动，确定性强。运行方式：
/// godot --headless --path &lt;project&gt; res://Scenes/TestFishMethods.tscn --quit-after 240
/// </summary>
public sealed partial class TestFishMethods : Node
{
    private const double Dt = 1.0 / 60.0;

    private int _passed;
    private int _failed;
    private Area2D _water = null!;

    public override void _Ready()
    {
        // 水域：水面 y=0、水底 y=400（全局），供 Fish/Hook 的水域矩形判定
        _water = new Area2D { Name = "Water", GlobalPosition = new Vector2(500, 200) };
        var cs = new CollisionShape2D { Name = "CollisionShape2D" };
        cs.Shape = new RectangleShape2D { Size = new Vector2(3000, 400) };
        _water.AddChild(cs);
        AddChild(_water);

        Run();
        GD.Print($"[FishMethodsTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishMethodsTest] ALL PASSED" : "[FishMethodsTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }

    private void Run()
    {
        TestDefaultEquivalence();
        TestBehaviorTakeover();
        TestStruggleTakeover();
        TestStruggleRelease();
        TestMouthChaseTarget();
        TestMouthMount();
        TestHookedFacing();
        TestEmptySlots();

        GD.Print($"[FishMethodsTest] 完成：PASS={_passed} FAIL={_failed}");
    }

    // ---- 建一条鱼（进树接线 Water/BaitSource；策略从 Def 两个槽带出，同 FishSpawner 路径；不断自动物理、保持手动 Step） ----

    private Fish MakeFish(Vector2 pos, FishDef def, Node2D? bait = null)
    {
        var fish = new Fish { Def = def, BehaviorStrategy = def.BehaviorStrategy, Water = _water, BaitSource = bait, GlobalPosition = pos, SpawnAtReady = false };
        fish.BehaviorData = (def.BehaviorStrategy as ClassicBehavior)?.ToData() ?? FishBehaviorData.Unconfigured;   // 数据预填（同生成器）
        AddChild(fish);
        fish.ProcessMode = ProcessModeEnum.Disabled;
        return fish;
    }

    private Hook MakeHook(Vector2 home)
    {
        var hook = new Hook
        {
            Position = home,
            Line = new FishingLineDef { Id = 99, DisplayName = "测试鱼线", MaxTension = 1000f, Length = 20000f },
        };
        AddChild(hook);
        hook.ProcessMode = ProcessModeEnum.Disabled;
        hook.ConnectWater(_water);
        return hook;
    }

    private static void DriveToBite(Fish fish)
    {
        for (var i = 0; i < 8 && fish.Current != Fish.State.Bitten; i++) fish.Step(Dt);
    }

    // ---- A. 默认等价：Def 挂上内置策略后照常巡逻 ----

    private void TestDefaultEquivalence()
    {
        var behavior = new ClassicBehavior
        {
            SwimDepthMin = 30f, SwimDepthMax = 120f, PatrolDistanceX = 100f,
            PatrolSpeed = 50f, ChaseSpeed = 200f, SenseRadius = 150f, ChaseRange = 300f,
        };
        var def = new FishDef
        {
            Id = 1, DisplayName = "普通鱼", MaxStamina = 10, MaxEndurance = 10, PullSpeed = 100,
            BehaviorStrategy = behavior,
        };
        var fish = MakeFish(new Vector2(500, 100), def);

        var inRange = true;
        for (var i = 0; i < 300; i++)
        {
            fish.Step(Dt);
            if (fish.Current != Fish.State.Patrol) { inRange = false; break; }
            var p = fish.GlobalPosition;
            if (p.X < 400f - 0.01f || p.X > 600f + 0.01f || p.Y < 30f - 0.01f || p.Y > 120f + 0.01f) { inRange = false; break; }
        }
        Check("A 内置策略：ClassicBehavior 的鱼照常巡逻且在矩形内（数据从策略参数流动）", inRange && fish.Current == Fish.State.Patrol);

        // Context.BehaviorData 跟随策略参数（鱼侧巡逻矩形/深度用同一份数据）
        var ctx = new FishContext(fish);
        Check("A 行为数据流到鱼上（ClassicBehavior 把自身参数打包进 Context.BehaviorData）",
            ctx.BehaviorData.PatrolDistanceX == 100f && ctx.BehaviorData.ChaseSpeed == 200f
            && fish.BehaviorData.SwimDepthMin == 30f && fish.BehaviorData.SwimDepthMax == 120f);
        fish.QueueFree();
    }

    // ---- B. 行为完全接管：自定义策略 ≠ 内置巡逻 ----

    private void TestBehaviorTakeover()
    {
        // 内置巡逻钳在巡逻矩形内（挂 ClassicBehavior 时由策略参数决定）；接管策略直线朝右游，越界也不钳。
        var strategy = new StraightBehavior();
        var def = new FishDef { Id = 2, DisplayName = "直行鱼", BehaviorStrategy = strategy };
        var fish = MakeFish(new Vector2(500, 100), def);

        for (var i = 0; i < 60; i++) fish.Step(Dt);   // 60 帧 = 1 秒
        Check("B 接管策略被逐帧调用（60 帧 = 60 次调用）", strategy.Steps == 60, $"实际 {strategy.Steps}");
        Check("B 鱼完全按自定义逻辑移动：直线右移 ≈ 120px", Mathf.Abs(fish.GlobalPosition.X - 620f) < 4f, $"实际 x={fish.GlobalPosition.X:F1}");
        Check("B 越出了默认巡逻钳制范围（[420,580] → 接管后不钳）", fish.GlobalPosition.X > 580f + 10f, $"实际 x={fish.GlobalPosition.X:F1}");
        Check("B 接管后状态机未被触碰（仍是 Patrol，鱼自己决定行为）", fish.Current == Fish.State.Patrol);
        Check("B 朝右移动 → 朝向朝右（FacingLeft = false，翻转机制仍由 Fish 保证）", !fish.FacingLeft);
        fish.QueueFree();
    }

    /// <summary>完全接管行为的策略：无视内置状态机，每帧直线朝右 120px/s（不钳巡逻矩形）。</summary>
    private sealed partial class StraightBehavior : FishBehaviorBase
    {
        public int Steps;

        public override void Update(FishContext ctx, double delta)
        {
            Steps++;
            ctx.MoveToward(ctx.Position + new Vector2(500f, 0f), 120f, (float)delta, patrolClamp: false);
        }
    }

    // ---- C. 挣扎完全接管：OnHooked + Update，钩子按自定义速度被拖走 ----

    private void TestStruggleTakeover()
    {
        var strategy = new JumperStruggle();
        var def = new FishDef
        {
            Id = 3, DisplayName = "跳跳鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 120,
            BehaviorStrategy = new ClassicBehavior(),   // 行为用内置（保证它能咬钩）；挣扎完全由下面接管
            Struggle = strategy,
        };
        var hook = MakeHook(new Vector2(600, 300));
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);   // 离起点 60px 处上钩
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);

        Check("C 前置：上钩成功", hook.BeginHookUp(fish));
        Check("C 上钩开场 OnHooked 被调用一次（上钩装配完成后）", strategy.HookedCalls == 1, $"实际 {strategy.HookedCalls}");

        var p0 = hook.GlobalPosition;
        for (var i = 0; i < 30; i++) hook.Step(Dt);   // 30 帧 = 0.5s
        var moved = hook.GlobalPosition - p0;
        Check("C 钩子按自定义 PullVelocity 直线向上拖 0.5s ≈ 60px", Mathf.Abs(moved.Y + 60f) < 3f && Mathf.Abs(moved.X) < 3f, $"实际位移 {moved}");
        Check("C 接管策略的每帧决策被调用（30 帧 = 30 次）", strategy.Steps == 30, $"实际 {strategy.Steps}");
        Check("C 数值机完全没被构建/触碰（体力/耐力仍在默认 0、拉力 0）",
            hook.Struggle.Stamina == 0f && hook.Struggle.Endurance == 0f && hook.Struggle.Tension == 0f);

        // 拖的过程中拉力不会无中生有（不经过数值机 → Released 永远 false，靠主动 RequestRelease）
        hook.QueueFree();
    }

    /// <summary>完全接管挣扎的策略：无视数值机，每帧决策 = 直线向上拖钩；开场计数 OnHooked。</summary>
    private sealed partial class JumperStruggle : FishStruggleBase
    {
        public int HookedCalls;
        public int Steps;

        public override void OnHooked(FishContext ctx) => HookedCalls++;

        public override void Update(FishContext ctx, double delta)
        {
            Steps++;
            ctx.PullVelocity = new Vector2(0f, -120f);   // 直线向上拖（无视数值机与方向轮切）
        }
    }

    // ---- C2. 主动脱钩：ReleaseRequested → 走清理链路 ----

    private void TestStruggleRelease()
    {
        var strategy = new EscapeStruggle();
        var def = new FishDef
        {
            Id = 4, DisplayName = "逃跑鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 120,
            BehaviorStrategy = new ClassicBehavior(),
            Struggle = strategy,
        };
        var hook = MakeHook(new Vector2(600, 300));
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);

        Check("C2 前置：上钩成功", hook.BeginHookUp(fish));
        for (var i = 0; i < 60; i++) hook.Step(Dt);   // 拖 1 秒（PullVelocity = 0 悬停）后 RequestRelease
        Check("C2 主动脱钩后：鱼离开钩子、装备清空（钩/饵/活饵/坠）",
            fish.GetParent() != hook
            && hook.HookDef == null && hook.Bait == null && hook.LiveBait == null && hook.Sinker == null
            && strategy.RequestedOnce);
        Check("C2 全程未碰数值机（体力仍在默认 0）", hook.Struggle.Stamina == 0f);

        var retracted = false;
        for (var i = 0; i < 600 && !retracted; i++) { hook.Step(Dt); if (hook.Current == Hook.State.Rest) retracted = true; }
        Check("C2 脱钩后鱼线自动收回 → 一竿结束（Rest）", retracted, $"实际 {hook.Current}");
        hook.QueueFree();
    }

    /// <summary>靠主动请求脱钩的挣扎策略：前 59 帧不拖不碰数值机，第 60 帧请求脱钩。</summary>
    private sealed partial class EscapeStruggle : FishStruggleBase
    {
        public bool RequestedOnce;
        private int _frames;

        public override void Update(FishContext ctx, double delta)
        {
            if (++_frames >= 60) { ctx.ReleaseRequested = true; RequestedOnce = true; }
        }
    }

    // ---- C3. 鱼嘴挂载锚点：上钩后鱼嘴（非鱼中心）对准钩子，且钩子移动/旋转后仍钉在嘴上 ----

    private void TestMouthMount()
    {
        var def = new FishDef
        {
            Id = 7, DisplayName = "嘴锚鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 120,
            MouthOffset = new Vector2(24f, 0f), SpriteScale = 6f,
            BehaviorStrategy = new ClassicBehavior(),
        };
        var hook = MakeHook(new Vector2(600, 300));
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(200, 40);   // 离起点明确偏右：拖行方向（离开起点）必朝右
        hook.SeedRandom(7);
        var fish = MakeFish(hook.GlobalPosition + new Vector2(60, 0), def, hook);
        new FishContext(fish).EnterBitten();   // 直接置咬中态（验证挂载锚点，不依赖追逐路径）
        fish.SetFacingLeft(true);              // 模拟「朝左游着咬钩」：上钩前的朝向不参与挂载
        Check("C3 前置：上钩成功", hook.BeginHookUp(fish));
        Check("C3 挂载后鱼嘴对准钩子（鱼中心 = 钩子 − 嘴偏移 144px）",
            (fish.MouthGlobalPosition - hook.GlobalPosition).Length() < 0.5f);
        Check("C3 咬钩后按拖行方向重算朝向（向左咬钩 → 被往右拖时朝右，不会倒着游）", !fish.FacingLeft);
        Check("C3 咬钩瞬间还没移动 → 鱼身保持水平（世界旋转 = 0）", Mathf.Abs(fish.GlobalRotation) < 0.001f,
            $"实际 {fish.GlobalRotation:F3} rad");

        // 按住左键收线（朝抛出起点 → 向左上）：钩子移动 + 旋转，鱼嘴必须一直钉在钩上、鱼身转到运动方向
        hook.ReelHeld = true;
        for (var i = 0; i < 30; i++) hook.Step(Dt);
        Check("C3 钩子移动后鱼嘴仍钉在钩上", (fish.MouthGlobalPosition - hook.GlobalPosition).Length() < 0.5f,
            $"偏移 {(fish.MouthGlobalPosition - hook.GlobalPosition).Length():F2}px");
        // 收线方向 = 起点 − 钩子 = (−200,−40)（左上方）；朝左（不镜像）→ θ = atan2(−d.Y, −d.X) = atan2(40,200) ≈ 11.3°
        var expectedAngle = Mathf.Atan2(40f, 200f);
        Check("C3 收线拖行 → 鱼身转到运动方向（左上），不继承钩子旋转", Mathf.Abs(fish.GlobalRotation - expectedAngle) < 0.01f,
            $"实际 {fish.GlobalRotation:F3} rad / 期望 {expectedAngle:F3} rad");
        var headDir = fish.FacingLeft
            ? -Vector2.Right.Rotated(fish.GlobalRotation)
            : Vector2.Right.Rotated(fish.GlobalRotation);
        Check("C3 鱼头指向运动方向（左上）", headDir.DistanceTo(new Vector2(-200f, -40f).Normalized()) < 0.01f,
            $"头方向 {headDir} / 运动方向 {new Vector2(-200f, -40f).Normalized()}");
        Check("C3 被收线向左拉 → 朝向重算为朝左（鱼朝前游）", fish.FacingLeft);
        hook.ReelHeld = false;
        hook.QueueFree();
    }

    // ---- C4. 追饵以鱼嘴为目标：没有咬中判定时，追到终点是「鱼嘴在饵上」而不是「鱼中心在饵上」 ----

    private void TestMouthChaseTarget()
    {
        var frames = new SpriteFrames();
        frames.AddAnimation("swim");
        frames.AddFrame("swim", ImageTexture.CreateFromImage(Image.CreateEmpty(100, 40, false, Image.Format.Rgba8)));   // 图片中心 (50,20)
        var behavior = new ClassicBehavior
        {
            SwimDepthMin = 20f, SwimDepthMax = 150f, PatrolDistanceX = 300f,
            PatrolSpeed = 60f, ChaseSpeed = 200f, SenseRadius = 600f, ChaseRange = 3000f,
        };
        var def = new FishDef
        {
            Id = 8, DisplayName = "嘴追鱼", MouthOffset = new Vector2(20f, 25f), SpriteScale = 6f,
            SpriteFrames = frames, BehaviorStrategy = behavior,
        };
        var fish = new Fish
        {
            Name = "MouthChaseFish", Def = def, BehaviorStrategy = behavior, Water = _water,
            GlobalPosition = new Vector2(500, 100), SpawnAtReady = false, BiteRange = 0f,   // 关掉咬中判定：让鱼一直追到终点
        };
        fish.BehaviorData = behavior.ToData();
        fish.AddChild(new AnimatedSprite2D { Name = "AnimatedSprite2D" });
        var bait = new Node2D { Name = "Bait", GlobalPosition = new Vector2(900, 100) };
        AddChild(bait);
        AddChild(fish);                 // _Ready 会用导出字段 BaitSource 覆盖连接，所以接线必须在进树之后
        fish.ConnectBait(bait);
        fish.ProcessMode = ProcessModeEnum.Disabled;

        for (var i = 0; i < 240 && fish.MouthGlobalPosition.DistanceTo(bait.GlobalPosition) > 0.5f; i++) fish.Step(Dt);

        var mouthGap = fish.MouthGlobalPosition.DistanceTo(bait.GlobalPosition);
        var centerGap = fish.GlobalPosition.DistanceTo(bait.GlobalPosition);
        Check("C4 追饵终点：鱼嘴落在饵上（误差 < 1px）", mouthGap < 1f, $"实际 {mouthGap:F1}px");
        Check("C4 追饵终点：鱼中心停在饵后方一个嘴偏移处（不是中心贴饵）",
            centerGap > 120f && Mathf.Abs(centerGap - fish.MouthWorldOffset.Length()) < 2f,
            $"中心距饵 {centerGap:F1}px / 嘴偏移 {fish.MouthWorldOffset.Length():F1}px");
        Check("C4 朝右追饵：嘴偏移 = (原图 20,25 − 中心 50,20)×6 镜像 → (180,30)",
            Mathf.Abs(fish.MouthWorldOffset.X - 180f) < 0.01f && Mathf.Abs(fish.MouthWorldOffset.Y - 30f) < 0.01f,
            $"实际 {fish.MouthWorldOffset}");
        fish.QueueFree();
        bait.QueueFree();
    }

    // ---- C5. 上钩拖行：鱼身整体转到运动方向（竖直分界定左右；右上/右下 → 头朝右，左上/左下 → 头朝左） ----

    private void TestHookedFacing()
    {
        var strategy = new FixedPullStruggle();
        var def = new FishDef
        {
            Id = 9, DisplayName = "拖行鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 120,
            MouthOffset = new Vector2(0f, 6f), SpriteScale = 6f,
            BehaviorStrategy = new ClassicBehavior(), Struggle = strategy,
        };
        var hook = MakeHook(new Vector2(600, 300));
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(200, 60);   // 起点右侧：首轮拖行方向必朝右
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        new FishContext(fish).EnterBitten();
        Check("C5 前置：上钩成功", hook.BeginHookUp(fish));

        // 朝右上拖：右半平面 → 头朝右；镜像后 θ = atan2(d.Y, d.X) = atan2(−120, 120) = −45°（鱼身指向右上）
        strategy.Velocity = new Vector2(120f, -120f);
        for (var i = 0; i < 12; i++) hook.Step(Dt);
        Check("C5 右上拖行：头朝右（FacingLeft = false，竖直分界）", !fish.FacingLeft);
        Check("C5 右上拖行：鱼身 = 运动方向（世界旋转 = −45°）", Mathf.Abs(fish.GlobalRotation + Mathf.Pi / 4f) < 0.01f,
            $"实际 {fish.GlobalRotation:F3} rad");
        Check("C5 右上拖行：鱼嘴仍钉在钩上（嘴偏移随鱼身旋转）",
            (fish.MouthGlobalPosition - hook.GlobalPosition).Length() < 0.5f,
            $"偏移 {(fish.MouthGlobalPosition - hook.GlobalPosition).Length():F2}px");

        // 朝左下拖：左半平面 → 头朝左；不镜像 θ = atan2(−d.Y, −d.X) = atan2(−120, 120) = −45°
        strategy.Velocity = new Vector2(-120f, 120f);
        for (var i = 0; i < 12; i++) hook.Step(Dt);
        Check("C5 左下拖行：头朝左（FacingLeft = true，竖直分界）", fish.FacingLeft);
        Check("C5 左下拖行：鱼身 = 运动方向（世界旋转 = −45°）", Mathf.Abs(fish.GlobalRotation + Mathf.Pi / 4f) < 0.01f,
            $"实际 {fish.GlobalRotation:F3} rad");
        var headDir = fish.FacingLeft
            ? -Vector2.Right.Rotated(fish.GlobalRotation)
            : Vector2.Right.Rotated(fish.GlobalRotation);
        Check("C5 左下拖行：鱼头指向运动方向（左下）", headDir.DistanceTo(new Vector2(-1f, 1f).Normalized()) < 0.01f,
            $"头方向 {headDir}");
        Check("C5 左下拖行：鱼嘴仍钉在钩上",
            (fish.MouthGlobalPosition - hook.GlobalPosition).Length() < 0.5f,
            $"偏移 {(fish.MouthGlobalPosition - hook.GlobalPosition).Length():F2}px");
        hook.QueueFree();
    }

    /// <summary>固定拖行速度的挣扎策略：直接输出设定速度（不碰数值机），用于验证上钩拖行的鱼身朝向。</summary>
    private sealed partial class FixedPullStruggle : FishStruggleBase
    {
        public Vector2 Velocity;

        public override void Update(FishContext ctx, double delta) => ctx.PullVelocity = Velocity;
    }

    // ---- D. 空槽语义：Behavior 空 = 不动只漂；Struggle 空 = 安静待收线 ----

    private void TestEmptySlots()
    {
        // D1. 行为空槽：挂了 Def 但没有 BehaviorStrategy → 鱼不动，只漂着（生成时打一条仅测试可见的警告）
        var dfish = new FishDef { Id = 5, DisplayName = "漂着鱼" };
        var fish = MakeFish(new Vector2(500, 100), dfish);
        var p0 = fish.GlobalPosition;
        for (var i = 0; i < 60; i++) fish.Step(Dt);
        Check("D1 空行为槽：鱼不动，只漂在原地（状态仍是 Patrol）",
            fish.GlobalPosition == p0 && fish.Current == Fish.State.Patrol);
        fish.QueueFree();

        // D2. 挣扎空槽：上钩后安静待收线（不拖钩子），按住左键可直接收线钓上（上钩时打一条仅测试可见的警告）
        var quietDef = new FishDef
        {
            Id = 6, DisplayName = "安静鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 120,
            BehaviorStrategy = new ClassicBehavior(),   // 行为照旧能咬钩；Struggle 槽故意留空
        };
        var hook = MakeHook(new Vector2(600, 300));
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);
        var q = MakeFish(hook.GlobalPosition + new Vector2(8, 4), quietDef, hook);
        DriveToBite(q);

        Check("D2 前置：上钩成功", hook.BeginHookUp(q));
        var hp0 = hook.GlobalPosition;
        for (var i = 0; i < 120; i++) hook.Step(Dt);   // 2 秒：无挣扎策略 → PullVelocity 恒 0 → 悬停原地
        Check("D2 空挣扎槽：钩子悬停不动（不拖、可安静收线）", (hook.GlobalPosition - hp0).Length() < 0.5f, $"位移 {(hook.GlobalPosition - hp0).Length():F2}px");
        Check("D2 数值机未被构建（体力/耐力仍在默认 0）", hook.Struggle.Stamina == 0f && hook.Struggle.Endurance == 0f);

        // 安静状态下按住左键 = 正常收线：无挣扎干扰，直接拉回起点钓上
        hook.ReelHeld = true;
        var caught = false;
        hook.Caught += _ => caught = true;
        var rested = false;
        for (var i = 0; i < 1200 && !rested; i++) { hook.Step(Dt); if (hook.Current == Hook.State.Rest) rested = true; }
        Check("D2 空挣扎槽：安静状态按左键收线 → 拉回起点钓上（整条链路可走）", rested && caught, $"Rest={rested} Caught={caught}");
        hook.ReelHeld = false;
        hook.QueueFree();
    }

    private static void SetEquipment(Hook hook)
    {
        hook.HookDef = new FishingHookDef { Id = 1, DisplayName = "测试钩" };
        hook.Bait = new BaitDef { Id = 1, DisplayName = "测试饵" };
        hook.LiveBait = new FishDef { Id = 22, DisplayName = "测试活饵" };
        hook.Sinker = new SinkerDef { Id = 1, DisplayName = "测试坠" };
    }

    private static void CastIntoSinking(Hook hook)
    {
        hook.Cast(0f);
        for (var i = 0; i < 600 && hook.Current != Hook.State.Sinking; i++) hook.Step(Dt);
    }
}
using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// 水体贴合测试（第二步：Fish / Hook / 决策上下文接入 WaterSpace）：
///  ▪ FishContext：行为策略能读到到水面/水底/左岸/右岸的距离、以及是否在水里；
///  ▪ 巡逻：坡底水域里随机巡逻目标全部落在水内（巡逻带的下界取整段 x 的最浅水底）；
///  ▪ 移动：撞水底/岸时钳在边界、并沿边界滑动，不会游出水；
///  ▪ MoveInside：位移限制 + 沿边滑动（挣扎拖钩共用同一套规则）；
///  ▪ Hook：下沉停在水底折线（湖床）而不是水区矩形底；
///  ▪ Hook：沉到水底后按住左键 = 直线拉离水底（不受重力）；松开左键立刻恢复重力，沿收紧后的线长
///    圆弧摆回、重新落回水底；
///  ▪ Hook：收线水面间隙 X 三档收线（深处直线朝锚点 / 骑在 X±10% 保持高度水平移近 / 太浅或水上收线
///    同时受重力下降），进入锚点正下方 ±LiftUnlockRange 忽略 X 直线收回；上钩的鱼可改、脱钩/钓上重设
///    （数值读改在 TestHookup）。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestFishWater.tscn --quit-after 240
/// </summary>
public sealed partial class TestFishWater : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        TestContextDistances();
        TestPatrolStaysInWater();
        TestMoveClampAndSlide();
        TestMoveInside();
        TestHookSinksToLakebed();
        TestReelOffBottomFallsBack();
        TestReelSurfaceTiers();

        GD.Print($"[FishWaterTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishWaterTest] ALL PASSED" : "[FishWaterTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    // ---- 上下文：策略能直接读到距离 ----

    private void TestContextDistances()
    {
        // 平水区：x ∈ [0, 1000)，水面 y=0，水底 y=400
        var water = MakeFlatWater("WaterA", new Vector2(500f, 200f), new Vector2(1000f, 400f));
        var fish = new Fish
        {
            Name = "FishA",
            ProcessMode = ProcessModeEnum.Disabled,
            SpawnAtReady = false,
            BehaviorStrategy = new ClassicBehavior(),
        };
        AddChild(fish);
        fish.ConnectWater(water);
        fish.GlobalPosition = new Vector2(250f, 150f);

        var ctx = new FishContext(fish);
        Check("上下文：拿到水体几何", ctx.Water != null);
        Check("上下文：在水里", ctx.IsInWater);
        Near("上下文：到水面距离", ctx.DistanceToSurface, 150f);
        Near("上下文：到水底距离", ctx.DistanceToBottom, 250f);
        Near("上下文：到左岸距离", ctx.DistanceToLeft, 250f);
        Near("上下文：到右岸距离", ctx.DistanceToRight, 750f);

        fish.GlobalPosition = new Vector2(250f, -50f);   // 水面上方
        Check("上下文：水面上方 → 不在水里", !ctx.IsInWater);
        Near("上下文：水面上方 → 到水面为负", ctx.DistanceToSurface, -50f);

        fish.GlobalPosition = new Vector2(250f, 450f);   // 水底下方
        Check("上下文：水底下方 → 不在水里", !ctx.IsInWater);
        Near("上下文：水底下方 → 到水底为负", ctx.DistanceToBottom, -50f);

        // 没有水域 → 距离为 +∞（策略可以据此判断「没有水」）
        var dryFish = new Fish
        {
            Name = "FishDry",
            ProcessMode = ProcessModeEnum.Disabled,
            SpawnAtReady = false,
            BehaviorStrategy = new ClassicBehavior(),
        };
        AddChild(dryFish);
        var dryCtx = new FishContext(dryFish);
        Check("上下文：无水域 → Water 为 null", dryCtx.Water == null);
        Check("上下文：无水域 → 距离为 +∞",
            float.IsPositiveInfinity(dryCtx.DistanceToSurface) && float.IsPositiveInfinity(dryCtx.DistanceToBottom));

        fish.QueueFree();
        dryFish.QueueFree();
        water.QueueFree();
    }

    // ---- 巡逻：坡底水域里整段巡逻路径都在水内 ----

    private void TestPatrolStaysInWater()
    {
        // 坡底：x ∈ [0, 1000)，水面 y=0，水底从 (0,100) 斜到 (1000,400)
        var slope = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 100f), new Vector2(1000f, 400f)]);
        var water = MakeLakebedWater("WaterB", new Vector2(500f, 200f), new Vector2(1000f, 400f), slope);

        var classic = new ClassicBehavior
        {
            SwimDepthMin = 10f,
            SwimDepthMax = 1000f,   // 故意给一个远超水深的深度：巡逻带要靠水底约束兜住
            PatrolDistanceX = 300f,
        };
        var fish = new Fish
        {
            Name = "FishB",
            ProcessMode = ProcessModeEnum.Disabled,
            SpawnAtReady = true,
            BehaviorStrategy = classic,
            BehaviorData = classic.ToData(),
            Water = water,
            GlobalPosition = new Vector2(850f, 200f),
        };
        AddChild(fish);   // _Ready：SpawnPoint = (850,200) → SpawnAtRandom 在巡逻带里随机就位

        Check("巡逻：出生后在巡逻带内（水内）", slope.IsWater(fish.GlobalPosition));

        var allInside = true;
        for (var i = 0; i < 300; i++)
        {
            if (!slope.IsWater(fish.RandomPointIn())) { allInside = false; break; }
        }
        Check("巡逻：300 个随机巡逻目标全部在水内（浅水侧也不穿底）", allInside);

        // 一直朝右下角（水外）游：patrolClamp 应把它钉在巡逻带里，仍在水中
        for (var i = 0; i < 60; i++) fish.MoveToward(new Vector2(5000f, 5000f), 1000f, 0.1f, patrolClamp: true);
        Check("巡逻：朝水外猛游 60 帧后仍在水中", slope.IsWater(fish.GlobalPosition),
            $"实际 {fish.GlobalPosition}（水底 y={slope.FloorYAt(fish.GlobalPosition.X):F1}）");

        fish.QueueFree();
        water.QueueFree();
    }

    // ---- 移动：撞边界钳住并沿边滑（追饵/返回不属于巡逻带，只受水体边界约束） ----

    private void TestMoveClampAndSlide()
    {
        var water = MakeFlatWater("WaterC", new Vector2(500f, 200f), new Vector2(1000f, 400f));
        var fish = new Fish
        {
            Name = "FishC",
            ProcessMode = ProcessModeEnum.Disabled,
            SpawnAtReady = false,
            BehaviorStrategy = new ClassicBehavior(),
            GlobalPosition = new Vector2(500f, 300f),
        };
        AddChild(fish);
        fish.ConnectWater(water);

        fish.MoveToward(new Vector2(500f, 900f), 1000f, 1f, patrolClamp: false);   // 一直向下
        Check("移动：撞水底 → 停在水底，不穿出去", fish.GlobalPosition.Y <= 400f && fish.GlobalPosition.Y > 399f,
            $"实际 y={fish.GlobalPosition.Y:F2}");

        fish.GlobalPosition = new Vector2(100f, 200f);
        fish.MoveToward(new Vector2(-900f, 200f), 1000f, 1f, patrolClamp: false);  // 一直向左
        Check("移动：撞左岸 → 停在岸边", fish.GlobalPosition.X >= 0f && fish.GlobalPosition.X < 1f,
            $"实际 x={fish.GlobalPosition.X:F2}");

        fish.GlobalPosition = new Vector2(900f, 350f);
        fish.MoveToward(new Vector2(5000f, 5000f), 1000f, 1f, patrolClamp: false); // 斜着撞水底/右岸
        Check("移动：斜撞边界 → 沿边滑动后仍在水内",
            fish.GlobalPosition.X >= 0f && fish.GlobalPosition.X <= 1000f &&
            fish.GlobalPosition.Y >= 0f && fish.GlobalPosition.Y <= 400f,
            $"实际 {fish.GlobalPosition}");

        fish.QueueFree();
        water.QueueFree();
    }

    // ---- MoveInside：位移限制 + 沿边滑动（鱼和挣扎拖钩共用） ----

    private void TestMoveInside()
    {
        var s = WaterSpace.Flat(0f, 1000f, 0f, 400f);

        var free = s.MoveInside(new Vector2(500f, 200f), new Vector2(10f, 20f));
        Near("MoveInside：水内位移原样返回 X", free.X, 10f);
        Near("MoveInside：水内位移原样返回 Y", free.Y, 20f);

        var down = s.MoveInside(new Vector2(500f, 300f), new Vector2(0f, 200f));
        Check("MoveInside：向下撞底 → 只走到水底", 300f + down.Y <= 400f && 300f + down.Y > 399f,
            $"实际 y={300f + down.Y:F2}");

        var right = s.MoveInside(new Vector2(900f, 200f), new Vector2(200f, 0f));
        Check("MoveInside：向右撞岸 → 只走到右岸", 900f + right.X <= 1000f && 900f + right.X > 999f,
            $"实际 x={900f + right.X:F2}");

        var slide = s.MoveInside(new Vector2(500f, 380f), new Vector2(100f, 100f));   // 斜向下撞底
        Check("MoveInside：斜撞水底 → 沿水底向右滑（不穿底且保留横向位移）",
            slide.X > 50f && 380f + slide.Y <= 400f && 380f + slide.Y > 399f,
            $"实际位移 {slide}");

        var shoreSlide = s.MoveInside(new Vector2(950f, 200f), new Vector2(100f, 100f));   // 斜向下撞右岸
        Check("MoveInside：斜撞右岸 → 沿岸向下滑（不穿岸且保留纵向位移）",
            shoreSlide.Y > 50f && 950f + shoreSlide.X <= 1000f && 950f + shoreSlide.X > 999f,
            $"实际位移 {shoreSlide}");
    }

    // ---- Hook：下沉停在水底折线（湖床），不是 Water 矩形底 ----

    private void TestHookSinksToLakebed()
    {
        // 湖床从 (0,150) 斜到 (1000,350)；Water 矩形底在 y=400 —— 两者必须能区分开
        var slope = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 150f), new Vector2(1000f, 350f)]);
        var water = MakeLakebedWater("WaterD", new Vector2(500f, 200f), new Vector2(1000f, 400f), slope);

        var hook = new Hook
        {
            Name = "HookD",
            ProcessMode = ProcessModeEnum.Disabled,
            GlobalPosition = new Vector2(200f, 100f),   // 已在水中（湖床在 x=200 处 y=190）
        };
        AddChild(hook);   // _Ready：_home = (200,100)
        hook.ConnectWater(water);
        hook.Cast(0f);
        hook.Step(1.0 / 60.0);   // 起飞即触水 → 进入下沉
        Check("Hook：入水后进入下沉", hook.Current == Hook.State.Sinking, $"实际 {hook.Current}");

        for (var i = 0; i < 1200 && hook.Current == Hook.State.Sinking; i++) hook.Step(1.0 / 60.0);

        Check("Hook：沉到底 → 停在收线停住态", hook.Current == Hook.State.Reeling && hook.Parked,
            $"实际 {hook.Current} Parked={hook.Parked}");
        var expected = slope.FloorYAt(hook.GlobalPosition.X);
        Check("Hook：停在水底折线上（不是矩形底 400）",
            Mathf.Abs(hook.GlobalPosition.Y - expected) < 1e-3f && expected < 400f,
            $"实际 y={hook.GlobalPosition.Y:F2}，湖床 y={expected:F2}");

        hook.QueueFree();
        water.QueueFree();
    }

    // ---- Hook：沉到水底后收线仍受重力（用户场景：沉底 → 收线拉离水底 → 松手 → 又落回水底） ----

    private void TestReelOffBottomFallsBack()
    {
        // 平水区：x ∈ [0,1000)，水面 y=0，水底 y=400；抛出起点在水面上方 (300,-50)
        // 钩子沉到水底时 x≈600 → 线长 r≈550，圆弧最低点 y≈500 低于水底 400 → 松手必然先碰到水底
        var water = MakeFlatWater("WaterE", new Vector2(500f, 200f), new Vector2(1000f, 400f));
        var hook = new Hook
        {
            Name = "HookE",
            ProcessMode = ProcessModeEnum.Disabled,
            GlobalPosition = new Vector2(300f, -50f),
        };
        AddChild(hook);   // _Ready：_home = (300,-50)
        hook.ConnectWater(water);
        hook.Cast(0f);    // 最小力：落点仍在水区内

        const float dt = 1f / 60f;
        for (var i = 0; i < 2400 && !hook.Parked; i++) hook.Step(dt);   // 飞 → 入水 → 沉底
        Check("沉底：钩子停在收线停住态（水底 y=400）",
            hook.Current == Hook.State.Reeling && hook.Parked && Mathf.Abs(hook.GlobalPosition.Y - 400f) < 1e-3f,
            $"实际 {hook.Current} Parked={hook.Parked} y={hook.GlobalPosition.Y:F2}");
        Check("沉底：钩子在水底下方以外、仍在水中 x 范围内",
            hook.GlobalPosition.X > 300f && hook.GlobalPosition.X < 1000f, $"实际 x={hook.GlobalPosition.X:F1}");

        // 收线一帧：离开水底、沿鱼线朝起点直线移动（速率 = ReelSpeed）
        var bottomPos = hook.GlobalPosition;
        var bottomR = (bottomPos - hook.CastOrigin).Length();
        hook.ReelHeld = true;
        hook.Step(dt);
        var reelStep = (hook.GlobalPosition - bottomPos).Length();
        Check("收线：沉底后也能被直线拉离水底（y 变小）", hook.GlobalPosition.Y < bottomPos.Y - 0.5f,
            $"水底 y={bottomPos.Y:F1} → {hook.GlobalPosition.Y:F1}");
        Check("收线：每帧位移 = ReelSpeed×dt（直线匀速，不受重力）",
            Mathf.Abs(reelStep - hook.ReelSpeed * dt) < 0.5f, $"实际 {reelStep:F2}");
        Check("收线：位移方向正对抛出起点",
            (hook.GlobalPosition - bottomPos).Normalized().Dot((hook.CastOrigin - bottomPos).Normalized()) > 0.999f);

        for (var i = 0; i < 3; i++) hook.Step(dt);   // 共收 4 帧 ≈ 13px：线长仍足够让圆弧碰到底
        var reelEnd = hook.GlobalPosition;
        var rRelease = (reelEnd - hook.CastOrigin).Length();
        Check("收线：线长随收线缩短", rRelease < bottomR - 5f, $"收线前 {bottomR:F1} → {rRelease:F1}");

        // 松开左键 → 立刻恢复重力：沿收紧后的线长圆弧摆回，重新落到水底
        hook.ReelHeld = false;
        for (var i = 0; i < 20; i++) hook.Step(dt);   // 0.333s 自由摆动（还没荡到水底）
        Check("松手：重力立刻把钩子往下拽（y 增大）", hook.GlobalPosition.Y > reelEnd.Y + 1f,
            $"松手 y={reelEnd.Y:F1} → {hook.GlobalPosition.Y:F1}");
        Check("松手：仍钉在收紧后的线长圆弧上",
            Mathf.Abs((hook.GlobalPosition - hook.CastOrigin).Length() - rRelease) < 1f,
            $"实际线长 {(hook.GlobalPosition - hook.CastOrigin).Length():F2} vs {rRelease:F2}");

        for (var i = 0; i < 2400 && !hook.Parked; i++) hook.Step(dt);
        Check("松手后：重新落回水底停住（Parked）", hook.Parked, $"实际 Parked={hook.Parked}");
        Check("松手后：高度回到水底 y=400（重力重新压回水底）",
            Mathf.Abs(hook.GlobalPosition.Y - 400f) < 1e-3f, $"实际 y={hook.GlobalPosition.Y:F2}");
        Check("落回水底时钩子位于起点正下方偏右（圆弧与 400 交点上）",
            hook.GlobalPosition.X > hook.CastOrigin.X && hook.GlobalPosition.X < reelEnd.X,
            $"实际 x={hook.GlobalPosition.X:F1}（起点 x={hook.CastOrigin.X:F1}，松手 x={reelEnd.X:F1}）");

        hook.QueueFree();
        water.QueueFree();
    }

    // ---- Hook：收线水面间隙 X —— 三档收线（深处直线 / 骑线 / 浅水或水上重力）+ 最终拉升解锁区 ----

    private void TestReelSurfaceTiers()
    {
        // 平水区：x ∈ [0, 5000)，水面 y=0，水底 y=400（足够宽：浅水/水上两段收线要先「边收边沉」再骑线到锚点）
        var water = MakeFlatWater("WaterF", new Vector2(2500f, 200f), new Vector2(5000f, 400f));
        const float dt = 1f / 60f;
        const float reelSpeed = 200f;   // Hook 默认 ReelSpeed

        // 每个场景一个钩子：抛出起点 home = (600,-100)（水面上方 100px）；ReelSurfaceGap X=60 → 容差带 [54,66]；
        // LiftUnlockRange 默认 50 → 「锚点正下方 ±50px」= x ∈ [550,650] 为最终拉升解锁区。
        // 圆弧收线模型的前提：线长 _radius = 钩子到锚点的实际距离。所以落水（Sinking）后先就地摆到目标位再 BeginReel
        // —— BeginReel 会以「当前位置到锚点距离」锁定线长，保证钩子从一开始就钉在「以锚点为圆心」的圆弧上。
        Hook NewReelHook(Vector2 target)
        {
            var h = new Hook
            {
                Name = "HookG",
                ProcessMode = ProcessModeEnum.Disabled,
                GlobalPosition = new Vector2(600f, -100f),
                ReelSurfaceGap = 60f,   // 测试场景专属 X：初始 = 当前 = 60
            };
            AddChild(h);
            h.ConnectWater(water);
            h.Cast(0f);
            for (var i = 0; i < 600 && h.Current == Hook.State.Flying; i++) h.Step(dt);   // 飞 → 入水
            for (var i = 0; i < 150 && h.Current == Hook.State.Sinking; i++) h.Step(dt);  // 多沉 2.5s：垂直速度回到水中终端 ≈112.5px/s（900×√1/8）
            h.GlobalPosition = target;   // 仍在 Sinking：就地落位（线长由 BeginReel 按新位置重新锁定）
            h.BeginReel();               // 进入收线模式，线长 = |target − home|
            return h;
        }

        var home = new Vector2(600f, -100f);

        // ① 深处（深度 350 > 带上沿 66）：直线朝锚点（旧行为不变）—— 收进容差带上沿转骑线 —— 进拉升区直线收回
        var h1 = NewReelHook(new Vector2(900f, 350f));   // 深度 350，横向 |dx|=300 > 50（拉升区外）
        h1.ReelHeld = true;
        var deepStart = h1.GlobalPosition;
        h1.Step(dt);
        var deepMove = h1.GlobalPosition - deepStart;
        Check("X深处：每帧位移 = ReelSpeed×dt（直线匀速，不受重力）",
            Mathf.Abs(deepMove.Length() - reelSpeed * dt) < 0.5f, $"实际 {deepMove.Length():F2}px");
        Check("X深处：位移方向正对抛出起点（直线回拉）",
            deepMove.Length() > 0.01f && deepMove.Normalized().Dot((home - deepStart).Normalized()) > 0.999f);

        var rode = false;
        var rideY = float.NaN;
        for (var i = 0; i < 400 && !rode; i++)
        {
            h1.Step(dt);
            if (h1.GlobalPosition.Y <= 66f) { rideY = h1.GlobalPosition.Y; rode = true; }   // 深度收进容差带（≤66）
        }
        Check("X深处：收线进容差带上沿（深度 66 附近）后停下直线", rode && rideY >= 63f && rideY <= 67f,
            $"进带 y={rideY:F2}");

        var rideX0 = h1.GlobalPosition.X;
        var rideY0 = h1.GlobalPosition.Y;
        for (var i = 0; i < 12; i++) h1.Step(dt);   // 骑线 12 帧（横移 ~40px，仍在拉升区外）
        Check("X骑线（带上沿进带）：高度冻结、水平移向锚点",
            Mathf.Abs(h1.GlobalPosition.Y - rideY0) < 0.05f && h1.GlobalPosition.X < rideX0 - 30f,
            $"y 漂移 {h1.GlobalPosition.Y - rideY0:F2}，x 前进 {rideX0 - h1.GlobalPosition.X:F1}px");
        Check("X骑线（带上沿进带）：横向仍在拉升区外（还没解锁拉升）",
            Mathf.Abs(h1.GlobalPosition.X - 600f) > 58f, $"dx={h1.GlobalPosition.X - 600f:F1}");

        for (var i = 0; i < 800 && h1.Current != Hook.State.Rest; i++) h1.Step(dt);
        Check("X骑线进拉升区 → 单摆+重力收短：一竿结束 Rest", h1.Current == Hook.State.Rest, $"实际 {h1.Current}");

        // ② 容差带内起始的三种深度（带下沿 54 / 带中 60 / 带上沿 66）都「保持高度、水平移向锚点」
        foreach (var y in new[] { 54f, 60f, 66f })
        {
            var h = NewReelHook(new Vector2(900f, y));
            h.ReelHeld = true;
            h.Step(dt);
            Check($"X带内 y={y:F0}：高度保持、水平移向锚点",
                Mathf.Abs(h.GlobalPosition.Y - y) < 0.05f
                && Mathf.Abs(h.GlobalPosition.X - (900f - reelSpeed * dt)) < 0.5f,
                $"位置 {h.GlobalPosition}");
        }

        // ③ 深度不足 X（水面下 20 < 带下沿，且离拉升区远）：收线同时按**空气重力**下降（需求 5：太浅一律空中，
        //    不区分水下/水上）→ 进带下沿转骑线收完
        var h3 = NewReelHook(new Vector2(2000f, 20f));   // 线长 = |(2000,20)−home| ≈ 1405：够长，圆弧有空间下沉进带
        h3.ReelHeld = true;
        h3.Step(dt);
        Check("X浅水：水平移向锚点 + 同时下沉（y 增大，收线不冻结高度）",
            h3.GlobalPosition.X < 2000f - 1f && h3.GlobalPosition.Y > 20f + 0.001f,
            $"位置 {h3.GlobalPosition}，vy={h3.Velocity.Y:F1}");
        var shallowEnter = false;
        var shallowY = float.NaN;
        for (var i = 0; i < 800 && !shallowEnter; i++)
        {
            if (h3.GlobalPosition.Y >= 53.5f) { shallowEnter = true; shallowY = h3.GlobalPosition.Y; }
            h3.Step(dt);
        }
        Check("X浅水：下沉到容差带下沿（y≈54）即转骑线", shallowEnter && shallowY >= 53f && shallowY <= 68f,
            $"进带 y={shallowY:F2}");
        var sY0 = h3.GlobalPosition.Y;
        var sX0 = h3.GlobalPosition.X;
        for (var i = 0; i < 40; i++) h3.Step(dt);   // 骑线 40 帧（横移 ~133px，离拉升区仍远）
        Check("X浅水转骑线：高度冻结（不再下沉）", Mathf.Abs(h3.GlobalPosition.Y - sY0) < 0.05f,
            $"y 漂移 {h3.GlobalPosition.Y - sY0:F2}");
        Check("X浅水转骑线：继续水平移向锚点", h3.GlobalPosition.X < sX0 - 100f,
            $"x 前进 {sX0 - h3.GlobalPosition.X:F1}px");
        for (var i = 0; i < 3000 && h3.Current != Hook.State.Rest; i++) h3.Step(dt);
        Check("X浅水收完：骑线进拉升区 → 单摆+重力收短 → Rest", h3.Current == Hook.State.Rest, $"实际 {h3.Current}");

        // ④ 水面上方（y=-40 < 0）：收线同时受抛竿同款空气重力下落（太浅一律空气，入水后仍按空气重力）→ 最终骑线收完
        var h4 = NewReelHook(new Vector2(4000f, -40f));   // 水上 40px，横向 3400、线长 ≈3400（足够远：先下落再骑过去）
        h4.ReelHeld = true;
        h4.Step(dt);
        Check("X水上：水平移向锚点 + 空气重力下落（y 增大、速度向下；纯直线收线会立刻上升）",
            h4.GlobalPosition.X < 4000f - 1f && h4.GlobalPosition.Y > -40f + 0.001f && h4.Velocity.Y > 0f,
            $"位置 {h4.GlobalPosition}，vy={h4.Velocity.Y:F1}");
        for (var i = 0; i < 4000 && h4.Current != Hook.State.Rest; i++) h4.Step(dt);
        Check("X水上收完：下落→入水→骑线→拉升区→单摆+重力收短 → Rest", h4.Current == Hook.State.Rest, $"实际 {h4.Current}");

        // ⑤ 最终拉升解锁区（锚点正下方 ±50）：无论多深都受重力的单摆收线（需求 6，取代旧「直线穿水面上提」）。
        // 钩子在弧底附近（(580,60)，圆弧最低点 = (600,61)）：重力切向分量≈0，主要靠「线缩短的径向收短」把钩子带向锚点——
        // 首帧位移 ≈ ReelSpeed×dt（线在收短）、方向「斜向上收、仍在水下」，且**不是严格直线**（底端只沿弧切向滑 + 径向收短带动）。
        var h5 = NewReelHook(new Vector2(580f, 60f));   // 深度 60（带内）+ |dx|=20 ≤ 50 → 拉升区优先 → 单摆而非直线拉升
        h5.ReelHeld = true;
        var liftStart = h5.GlobalPosition;
        h5.Step(dt);
        var liftMove = h5.GlobalPosition - liftStart;
        Check("X拉升区单摆：每帧位移 = ReelSpeed×dt（线在匀速收短）",
            Mathf.Abs(liftMove.Length() - reelSpeed * dt) < 0.5f, $"实际 {liftMove.Length():F2}px");
        Check("X拉升区单摆：首帧斜向上收（y 减小、仍在水下，不是无视重力直线射到水面）",
            liftMove.Y < 0f && h5.GlobalPosition.Y > 20f, $"位移 {liftMove}，y={h5.GlobalPosition.Y:F1}");
        var liftDot = liftMove.Length() > 0.01f ? liftMove.Normalized().Dot((home - liftStart).Normalized()) : 1f;
        Check("X拉升区单摆：方向不是严格直线（底端沿弧切向滑 + 线缩短带动，旧直线拉升为 1.000）",
            liftMove.Length() > 0.01f && liftDot < 0.999f,
            $"方向点积 {liftDot:F4}");
        for (var i = 0; i < 300 && h5.Current != Hook.State.Rest; i++) h5.Step(dt);
        Check("X拉升区单摆：线收短拖回起点 → Rest", h5.Current == Hook.State.Rest, $"实际 {h5.Current}");

        // ⑥ 最终拉升解锁区内、但钩子在水面上方很高（深度 << 带上沿，且圆弧上有向下摆的空间）：
        // 依旧走拉升区单摆（需求 6：任何深度都受重力，这里太浅 → 空气重力）——先受空气重力沿圆弧下摆（y 增大），
        // 线随收线缩短把钩子收向锚点（单摆公式钉回弧上，绝无直线穿水面）。
        var h6 = NewReelHook(new Vector2(620f, -300f));   // |dx|=20 ≤ 50（拉升区内）、线长 ≈201、深度 −300（水上 300px）
        h6.ReelHeld = true;
        var liftShallowStart = h6.GlobalPosition;
        h6.Step(dt);
        var liftShallowMove = h6.GlobalPosition - liftShallowStart;
        Check("X拉升区水上：先受空气重力沿圆弧下摆（y 增大），不再无视重力直线上升",
            liftShallowMove.Y > 0.01f && h6.Velocity.Y > 0f
            && liftShallowMove.Normalized().Dot((home - liftShallowStart).Normalized()) < 0.999f,
            $"位移 {liftShallowMove}，vy={h6.Velocity.Y:F1}");
        for (var i = 0; i < 3000 && h6.Current != Hook.State.Rest; i++) h6.Step(dt);
        Check("X拉升区水上收完：空气重力下摆后线收短拖回锚点 → Rest", h6.Current == Hook.State.Rest, $"实际 {h6.Current}");

        water.QueueFree();
    }

    // ---- 测试工具 ----

    private Area2D MakeFlatWater(string name, Vector2 center, Vector2 size)
    {
        var water = new Area2D { Name = name, GlobalPosition = center };
        water.AddChild(new CollisionShape2D
        {
            Name = "CollisionShape2D",
            Shape = new RectangleShape2D { Size = size },
        });
        AddChild(water);
        return water;
    }

    /// <summary>WaterBody + 矩形碰撞体 + 直接注入的湖床几何（等价于场景里挂 Lakebed 折线）。</summary>
    private WaterBody MakeLakebedWater(string name, Vector2 center, Vector2 size, WaterSpace space)
    {
        var water = new WaterBody { Name = name, GlobalPosition = center };
        water.AddChild(new CollisionShape2D
        {
            Name = "CollisionShape2D",
            Shape = new RectangleShape2D { Size = size },
        });
        AddChild(water);      // _Ready 先按矩形烘焙平水底
        water.SetSpace(space); // 再换成带湖床的几何
        return water;
    }

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name} {detail}"); }
    }

    private void Near(string name, float actual, float expected) => Check(name, Mathf.Abs(actual - expected) < 1e-3f, $"实际 {actual:F3} 期望 {expected:F3}");
}

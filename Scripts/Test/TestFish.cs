using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// 鱼行为（Fish.cs + 行为策略）测试：实例化 Fish 节点（代码建 Water 水域 + 饵载体，注入依赖），验证：
///  ▪ 行为来自策略：挂内置 ClassicBehavior（无数据）→ 用内置默认参数照常游；带参数的策略数据全生效；
///  ▪ 空槽（无行为策略）→ 鱼不动只漂着；
    ///  ▪ 巡逻：只在水面下深度区间 ± 出生点左右巡逻范围内随机选点，不离开矩形；
///  ▪ 刷新位置 = 同巡逻矩形随机点（y 受深度区间约束，决定鱼刷新在哪个高度/左右范围）；
///  ▪ 感应：饵在水里且距鱼 &lt; 感知半径 → 进入追逐（追饵速度，明显快于巡逻速度）；
///  ▪ 追逐距离：追饵时离出生点超过追逐距离 → 放弃，直线回出生点附近 → 重新巡逻（回巢再战，无冷却）；
///  ▪ 咬中：追饵追到与饵距离 &lt; BiteRange → 直接判定 Bitten（事件触发）；
///  ▪ 转向：移动方向水平分量决定朝向（FacingLeft），精灵用 Scale.X 的符号镜像；每次左右翻转时
///    Scale.X 在 FishDef.TurnDuration 内线性从 +S 扫到 −S（转身动画）。
///
/// 鱼的运动自包含（Fish.Step 手动步进）：测试把 ProcessMode 关掉后逐帧 Step(delta)，完全确定性。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestFish.tscn --quit-after 240
/// </summary>
public sealed partial class TestFish : Node
{
    private int _passed;
    private int _failed;

    private Area2D _water = null!;
    private Node2D _bait = null!;

    /// <summary>固定行为策略参数：深度 30..120（水面下），左右范围 ±100，巡逻 50 / 追饵 200，感应 150，追逐距离 300。</summary>
    private static ClassicBehavior MakeBehavior() => new()
    {
        SwimDepthMin = 30f,
        SwimDepthMax = 120f,
        PatrolDistanceX = 100f,
        PatrolSpeed = 50f,
        ChaseSpeed = 200f,
        SenseRadius = 150f,
        ChaseRange = 300f,
    };

    public override void _Ready()
    {
        // 造一个水域（Area2D + 矩形碰撞体，供 Fish.WaterRect 几何读取）：水面 y=0、水底 y=400、左右 −1000..2000（全局）
        // 宽度留足，让「追过头 > 追逐距离」测试里鱼追出去时饵不至于先飞出水面（否则会误判为饵失效）。
        _water = new Area2D { GlobalPosition = new Vector2(500, 200) };
        var cs = new CollisionShape2D { Name = "CollisionShape2D" };
        cs.Shape = new RectangleShape2D { Size = new Vector2(3000, 400) };
        _water.AddChild(cs);
        AddChild(_water);

        // 饵载体：任意 Node2D，测试里移动它的位置模拟钩子上的饵
        _bait = new Node2D();
        AddChild(_bait);

        Run();
        GD.Print($"[FishTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishTest] ALL PASSED" : "[FishTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>建一条鱼：部署在指定位置（SpawnPoint = 出生点锚），断掉自动物理、锁随机序列保证可重现。
    /// 行为 = 策略（ClassicBehavior 或自定义；null = 空槽不动）；策略自身带数据（ClassicBehavior 的导出参数）。</summary>
    private Fish MakeFish(Vector2 home, FishBehaviorBase? strategy = null)
    {
        var fish = new Fish { GlobalPosition = home, SpawnAtReady = false, BehaviorStrategy = strategy };
        AddChild(fish);              // _Ready：SpawnPoint = GlobalPosition
        fish.SeedRandom(20261028);   // 固定随机序列，测试可重现
        fish.BehaviorData = (strategy as ClassicBehavior)?.ToData() ?? FishBehaviorData.Unconfigured;   // 数据预填（同生成器）
        fish.ProcessMode = ProcessModeEnum.Disabled;
        fish.ConnectWater(_water);
        fish.ConnectBait(_bait);
        return fish;
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    private void Run()
    {
        var behavior = MakeBehavior();
        Check("行为定义深度 30..120 正确读回", behavior.SwimDepthMin == 30f && behavior.SwimDepthMax == 120f);
        Check("行为定义巡逻 50 / 追饵 200 正确读回", behavior.PatrolSpeed == 50f && behavior.ChaseSpeed == 200f);
        Check("行为定义感应 150 / 追逐距离 300 正确读回", behavior.SenseRadius == 150f && behavior.ChaseRange == 300f);

        const double dt = 1.0 / 60.0;

        // ---- 1. 挂 ClassicBehavior（无数据）：内置默认参数也能动 ----
        var bare = MakeFish(new Vector2(500, 100), new ClassicBehavior());
        bare.Step(dt);
        Check("挂 ClassicBehavior（无数据）：默认进入巡逻（内置默认参数也能动）", bare.Current == Fish.State.Patrol);
        bare.QueueFree();

        // ---- 1b. 空槽（不挂任何策略）：鱼不动，只漂在原地 ----
        var still = MakeFish(new Vector2(300, 150));
        var stillPos = still.GlobalPosition;
        for (var i = 0; i < 60; i++) still.Step(dt);
        Check("空槽（无行为策略）：鱼不动，只漂在原地", still.Current == Fish.State.Patrol && still.GlobalPosition == stillPos);
        still.QueueFree();

        // ---- 2. 巡逻：只在水面下深度区间 ± 出生点左右范围内游 ----
        var fish = MakeFish(new Vector2(500, 100), behavior);
        bool inRange = true;
        for (int i = 0; i < 600; i++)
        {
            fish.Step(dt);
            if (fish.Current != Fish.State.Patrol) { inRange = false; break; }
            var p = fish.GlobalPosition;
            // 巡逻矩形：x ∈ [400,600]（SpawnPoint 500 ±100），y ∈ [30,120]（水面 0 + 深度区间）—— 水面 y=0
            if (p.X < 400f - 0.01f || p.X > 600f + 0.01f || p.Y < 30f - 0.01f || p.Y > 120f + 0.01f)
            {
                inRange = false;
                break;
            }
        }
        Check("巡逻 10 秒：始终在深度区间 + 左右巡逻范围内且不离开巡逻", inRange);
        Check("巡逻后状态仍为 Patrol", fish.Current == Fish.State.Patrol);

        // ---- 3. 刷新位置：同巡逻矩形随机点（y 在深度区间 = 决定刷新的高度） ----
        fish.SpawnAtRandom();
        var sp = fish.GlobalPosition;
        Check("刷新位置 x ∈ [400,600]（巡逻范围左右界）", sp.X is >= 400f - 0.01f and <= 600f + 0.01f);
        Check("刷新位置 y ∈ [30,120]（深度区间决定刷新高度）", sp.Y is >= 30f - 0.01f and <= 120f + 0.01f);

        // ---- 4. 感应 → 追逐：饵在水里且距离 < 感知半径 → 以追饵速度接近 ----
        _bait.GlobalPosition = fish.GlobalPosition + new Vector2(60, 0);   // 相对鱼位置放置 → 必然在感应半径内，且在水里
        fish.Step(dt);   // 巡逻帧检测到饵 → 进入追逐
        var before = fish.GlobalPosition;
        fish.Step(dt);   // 追逐帧朝饵移动
        var perFrame = before.DistanceTo(fish.GlobalPosition);
        Check("饵入感知半径 → 进入追逐", fish.Current == Fish.State.Chase);
        Check($"单帧位移 ≈ 追饵速度 200/60（实际 {perFrame:F2}，巡逻 50/60 做不到）", perFrame > 2f && perFrame < 4f);

        // ---- 5. 追逐距离：追饵途中离出生点超过 ChaseRange → 放弃返回 ----
        bool uTurn = false;
        for (int i = 0; i < 3000; i++)
        {
            fish.Step(dt);
            if (fish.Current == Fish.State.Returning) { uTurn = true; break; }
            // 鱼每帧追着饵走；把饵继续放远，逼鱼追到离出生点超过追逐距离(300)
            _bait.GlobalPosition = fish.GlobalPosition + new Vector2(400, 0);
        }
        Check("追过头（离出生点 > 追逐距离 = 300）→ 放弃进入返回",
            uTurn && fish.GlobalPosition.DistanceTo(fish.SpawnPoint) > 300f);

        // ---- 6. 返回：直线回出生点附近 → 重新巡逻（无冷却，回巢再战） ----
        bool regained = false;
        for (int i = 0; i < 2000; i++)
        {
            fish.Step(dt);
            if (fish.Current == Fish.State.Patrol) { regained = true; break; }
        }
        Check("返回后回到出生点附近 → 重新巡逻（无冷却）",
            regained && Mathf.Abs(fish.GlobalPosition.X - 500f) < 60f && Mathf.Abs(fish.GlobalPosition.Y - 100f) < 60f);

        // ---- 7. 咬中：追饵贴脸（距离 < BiteRange）→ 直接判定 Bitten + 事件 ----
        _bait.GlobalPosition = fish.GlobalPosition + new Vector2(8, 0);   // 8 < BiteRange 16
        int bites = 0;
        fish.Bitten += () => bites++;
        for (int i = 0; i < 300 && fish.Current != Fish.State.Bitten; i++) fish.Step(dt);
        Check("追饵贴脸（8 < BiteRange 16）→ 直接判定咬中", fish.Current == Fish.State.Bitten);
        Check("咬中事件触发 1 次", bites == 1);
        fish.QueueFree();

        // ---- 8. 转向：移动方向水平分量决定朝向（FacingLeft → 精灵 Scale.X 符号镜像） ----
        var flip = MakeFish(new Vector2(200, 100), behavior);
        _bait.GlobalPosition = flip.GlobalPosition + new Vector2(80, 0);   // 正右、距 80（< 感应、未贴脸）→ 朝右追
        for (int i = 0; i < 5; i++) flip.Step(dt);
        Check("朝右追饵：朝向朝右（FacingLeft = false）", !flip.FacingLeft);

        _bait.GlobalPosition = flip.GlobalPosition + new Vector2(-80, 0);  // 突然甩到左边 → 开始朝左追
        for (int i = 0; i < 5; i++) flip.Step(dt);
        Check("饵甩到左边：朝向朝左（FacingLeft = true）", flip.FacingLeft);
        flip.QueueFree();

        // ---- 8b. 低速也会转身（回归：旧 >1f 阈值会让低速每帧位移 ≤1px 的鱼永不翻转） ----
        var slow = MakeFish(new Vector2(200, 100), new ClassicBehavior
        {
            SwimDepthMin = 30f, SwimDepthMax = 120f, PatrolDistanceX = 100f,
            PatrolSpeed = 50f, ChaseSpeed = 30f, SenseRadius = 150f, ChaseRange = 300f,
        });
        _bait.GlobalPosition = slow.GlobalPosition + new Vector2(-80, 0);   // 正左、低速追（30px/s → 0.5px/帧）
        for (int i = 0; i < 5; i++) slow.Step(dt);
        Check("低速追饵（30px/s）仍会朝左转身（FacingLeft = true）", slow.FacingLeft);
        slow.QueueFree();

        // ---- 10. 鱼嘴锚点：MouthOffset 相对图片左上角(0,0)，减图片中心 × SpriteScale；图片左边=前方（朝左不镜像、朝右镜像 X） ----
        var mouthFrames = new SpriteFrames();
        mouthFrames.AddAnimation("swim");
        mouthFrames.AddFrame("swim", ImageTexture.CreateFromImage(Image.CreateEmpty(100, 40, false, Image.Format.Rgba8)));   // 100×40 → 图片中心 = (50, 20)
        var mouthDef = new FishDef
        {
            Id = 10, DisplayName = "嘴锚鱼", MouthOffset = new Vector2(20f, 25f), SpriteScale = 6f,   // 嘴在图片左半边（x=20 < 中心 50 = 前方）
            SpriteFrames = mouthFrames, BehaviorStrategy = behavior,
            TurnDuration = 0f,   // 本段只验证镜像数学 → 关掉转身过渡（瞬时翻转，5 帧后偏移就是稳态值）
        };
        var mouthFish = new Fish
        {
            Name = "MouthFish", Def = mouthDef, GlobalPosition = new Vector2(300, 100),
            SpawnAtReady = false, BehaviorStrategy = behavior,
        };
        mouthFish.BehaviorData = behavior.ToData();
        var mouthSprite = new AnimatedSprite2D { Name = "AnimatedSprite2D" };
        mouthFish.AddChild(mouthSprite);
        AddChild(mouthFish);   // _Ready 触发 SetupSprite：_spritePixelSize = (100, 40)
        mouthFish.ProcessMode = ProcessModeEnum.Disabled;
        mouthFish.ConnectWater(_water);
        mouthFish.ConnectBait(_bait);
        mouthFish.SeedRandom(20261028);
        Check("鱼嘴世界偏移（朝右，翻转）= (20−50, 25−20) 镜像 X → (30,5)×6 = (180, 30)",
            Mathf.Abs(mouthFish.MouthWorldOffset.X - 180f) < 0.01f && Mathf.Abs(mouthFish.MouthWorldOffset.Y - 30f) < 0.01f);
        _bait.GlobalPosition = mouthFish.GlobalPosition + new Vector2(-80, 0);   // 甩左边 → 朝左
        for (int i = 0; i < 5; i++) mouthFish.Step(dt);
        Check("朝左（不翻转）鱼嘴偏移 X 不镜像 → (−180, 30)，Y 不变（30）",
            Mathf.Abs(mouthFish.MouthWorldOffset.X + 180f) < 0.01f && Mathf.Abs(mouthFish.MouthWorldOffset.Y - 30f) < 0.01f);
        mouthFish.QueueFree();

        // ---- 9. 动画：Def 的 SpriteFrames 赋给 AnimatedSprite2D，三档播放速度按状态切换 ----
        var frames = new SpriteFrames();
        var animDef = new FishDef
        {
            Id = 9,
            DisplayName = "动画鱼",
            SpriteFrames = frames,
            NormalPlaySpeed = 1.5f,
            StrugglePlaySpeed = 2.5f,
            ExhaustedPlaySpeed = 0.4f,
        };
        var animFish = new Fish { Name = "AnimFish", Def = animDef, SpawnAtReady = false };
        var sprite = new AnimatedSprite2D { Name = "AnimatedSprite2D" };
        animFish.AddChild(sprite);
        AddChild(animFish);   // _Ready：解析 AnimatedSprite2D，赋 SpriteFrames + 初始速度
        Check("动画：_Ready 把 Def 精灵表赋给 AnimatedSprite2D", ReferenceEquals(sprite.SpriteFrames, frames));
        Check("动画：初始速度 = 正常播放速度", Mathf.Abs(sprite.SpeedScale - 1.5f) < 0.001f);

        animFish.ApplyPlayback(FishPlayback.Struggle);
        Check("动画：挣扎状态 → 挣扎速度", Mathf.Abs(sprite.SpeedScale - 2.5f) < 0.001f);
        animFish.ApplyPlayback(FishPlayback.Exhausted);
        Check("动画：虚弱状态 → 虚弱速度", Mathf.Abs(sprite.SpeedScale - 0.4f) < 0.001f);
        animFish.ApplyPlayback(FishPlayback.Normal);
        Check("动画：正常状态 → 正常速度", Mathf.Abs(sprite.SpeedScale - 1.5f) < 0.001f);
        animFish.QueueFree();

        // ---- 11. 转身动画：左右翻转时精灵 Scale.X 在 FishDef.TurnDuration 内线性从 +S 扫到 −S（经过 0 = 侧身） ----
        var turnFrames = new SpriteFrames();
        turnFrames.AddAnimation("swim");
        turnFrames.AddFrame("swim", ImageTexture.CreateFromImage(Image.CreateEmpty(16, 9, false, Image.Format.Rgba8)));
        var turnDef = new FishDef
        {
            Id = 11, DisplayName = "转身鱼", SpriteFrames = turnFrames, SpriteScale = 6f,
            TurnDuration = 0.5f,   // 每鱼可配：本测试用 0.5s 验证「时长来自 FishDef」
        };
        var turnFish = new Fish { Name = "TurnFish", Def = turnDef, SpawnAtReady = false };   // 无行为策略 → 不自己移动，只测转身
        var turnSprite = new AnimatedSprite2D { Name = "AnimatedSprite2D" };
        turnFish.AddChild(turnSprite);
        AddChild(turnFish);   // _Ready：初始 FacingLeft = false（朝右）→ Scale.X = −6（镜像）
        turnFish.ProcessMode = ProcessModeEnum.Disabled;
        Check("转身动画：初始朝右 → Scale.X = −SpriteScale（镜像）", Mathf.Abs(turnSprite.Scale.X + 6f) < 0.001f);
        Check("转身动画：FlipH 不用（恒 false，朝向统一由 Scale.X 符号表示）", !turnSprite.FlipH);

        turnFish.SetFacingLeft(true);   // 起转：朝左
        Check("转身动画：逻辑朝向立即切换（FacingLeft = true）", turnFish.FacingLeft);
        Check("转身动画：起转瞬间视觉仍在旧的 −6（0.5s 内线性过渡，不是瞬切）", Mathf.Abs(turnSprite.Scale.X + 6f) < 0.001f);
        for (int i = 0; i < 15; i++) turnFish.Step(dt);   // 0.25s = TurnDuration 0.5s 的一半
        Check("转身动画：半程 Scale.X ≈ 0（线性穿过 0 = 鱼身压成一条竖线）", Mathf.Abs(turnSprite.Scale.X) < 0.2f);
        for (int i = 0; i < 15; i++) turnFish.Step(dt);   // 再 0.25s → 0.5s 转完
        Check("转身动画：按 TurnDuration（0.5s）转完 → Scale.X = +6（朝左不镜像）", Mathf.Abs(turnSprite.Scale.X - 6f) < 0.001f);
        turnFish.SetFacingLeft(true);   // 同一朝向重复设置不重启动画
        turnFish.Step(dt);
        Check("转身动画：同一朝向重复设置不重启动画（Scale.X 保持 +6）", Mathf.Abs(turnSprite.Scale.X - 6f) < 0.001f);
        turnFish.QueueFree();

        // 11b. TurnDuration = 0 → 瞬时翻转（旧行为）
        var instantDef = new FishDef
        {
            Id = 12, DisplayName = "瞬转鱼", SpriteFrames = turnFrames, SpriteScale = 6f, TurnDuration = 0f,
        };
        var instantFish = new Fish { Name = "InstantTurnFish", Def = instantDef, SpawnAtReady = false };
        var instantSprite = new AnimatedSprite2D { Name = "AnimatedSprite2D" };
        instantFish.AddChild(instantSprite);
        AddChild(instantFish);
        instantFish.ProcessMode = ProcessModeEnum.Disabled;
        instantFish.SetFacingLeft(true);
        Check("转身动画：TurnDuration = 0 → 瞬时翻转（Scale.X 立刻 = +6）", Mathf.Abs(instantSprite.Scale.X - 6f) < 0.001f);
        instantFish.QueueFree();
    }
}
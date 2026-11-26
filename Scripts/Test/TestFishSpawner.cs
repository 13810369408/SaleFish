using System.Collections.Generic;
using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// 鱼类生成器（FishSpawner）测试：验证检查器配置的 SpawnPool 按 CopiesPerDef 生成对应数量的鱼、
/// 行为数据从策略实例带出（策略挂 ClassicBehavior 能游；空策略 = 不动只漂也能生成）、自动接线
/// （Water 深度/水面、BaitSource 感应饵）、
/// 鱼的出生刷新位置落在水域内且 y 服从各自深度区间、同种多条横向错开、prefab 生成（鱼带
/// AnimatedSprite2D + CollisionShape2D 结构）、无池无目录时不崩。
/// 测试不实例化正式关卡，自己搭迷你关卡（根=tests 节点 + Water/Hook/FishSpawner 子节点），
/// 走「生成器自动在父节点下找 Water/Hook」同 main_stage 一样的路径。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestFishSpawner.tscn --quit-after 240
/// </summary>
public sealed partial class TestFishSpawner : Node
{
    private int _passed;
    private int _failed;

    private Area2D _water = null!;
    private Area2D _hook = null!;

    /// <summary>固定鱼行为策略（数据在策略上）：深度 70..150（水面 y=0 起算）、巡逻范围 ±200。</summary>
    private readonly ClassicBehavior _carpBehavior = new()
    {
        SwimDepthMin = 70f,
        SwimDepthMax = 150f,
        PatrolDistanceX = 200f,
    };

    public override void _Ready()
    {
        // 迷你主关卡层级：Water(Area2D 矩形) + Hook(饵载体) + FishSpawner 都是本根节点的子节点
        _water = new Area2D { Name = "Water", GlobalPosition = new Vector2(500, 200) };
        var cs = new CollisionShape2D { Name = "CollisionShape2D" };
        cs.Shape = new RectangleShape2D { Size = new Vector2(2000, 400) };   // 水面 y=0、水底 y=400、左右 −500..1500
        _water.AddChild(cs);
        AddChild(_water);

        _hook = new Area2D { Name = "Hook", GlobalPosition = new Vector2(300, 92) };
        AddChild(_hook);

        Run();
        GD.Print($"[FishSpawnerTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishSpawnerTest] ALL PASSED" : "[FishSpawnerTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    /// <summary>建生成器并让它 _Ready（AddChild 触发）：池 + 每类条数；随即停掉自动物理（生成的鱼继承 Disabled，步进才动）。</summary>
    private FishSpawner MakeSpawner(FishDef[] pool, int copies)
    {
        var spawner = new FishSpawner { Name = "FishSpawner", SpawnPool = pool, CopiesPerDef = copies };
        AddChild(spawner);   // _Ready：扫父节点下的 Water/Hook 并接线 → 生成鱼
        spawner.ProcessMode = ProcessModeEnum.Disabled;
        return spawner;
    }

    private List<Fish> SpawnedFish(FishSpawner spawner)
    {
        var list = new List<Fish>();
        foreach (var child in spawner.GetChildren())
        {
            if (child is Fish f) list.Add(f);
        }
        return list;
    }

    /// <summary>迷你关卡里 Water 的全局包围盒（与生成器 WaterRect 同逻辑）。</summary>
    private Rect2 WaterRect()
    {
        var cs = _water.GetNode<CollisionShape2D>("CollisionShape2D");
        var shape = (RectangleShape2D)cs.Shape;
        var size = shape.Size * cs.GlobalScale;
        return new Rect2(cs.GlobalPosition - size / 2f, size);
    }

    private void Run()
    {
        // ---- 1. 默认值 ----
        var bare = new FishSpawner();
        Check("生成器默认池为空（未定义任何鱼）", bare.SpawnPool.Length == 0);
        Check("生成器默认每种生成 1 条", bare.CopiesPerDef == 1);
        bare.Free();

        // ---- 2. 多种鱼 × 多条：数量、行为数据、接线、位置 ----
        var carp = new FishDef
        {
            Id = 1, DisplayName = "鲤鱼",
            BehaviorStrategy = _carpBehavior,   // 行为策略自带数据：深度/巡逻范围都在策略实例上
        };
        var minnow = new FishDef { Id = 2, DisplayName = "小鱼" };   // 无策略无数据 → 生成但不动只漂

        var spawner = MakeSpawner(new[] { carp, minnow }, copies: 2);
        var fishes = SpawnedFish(spawner);

        Check("两种鱼 × 2 条 = 4 条鱼", fishes.Count == 4);

        int carpCount = 0, bareCount = 0;
        foreach (var f in fishes)
        {
            if (ReferenceEquals(f.BehaviorStrategy, _carpBehavior)) carpCount++;
            if (f.BehaviorData.PatrolDistanceX <= 0f) bareCount++;   // 无策略 → Unconfigured：横向范围 0 = 未配置
        }
        Check("鲤鱼 × 2 条都带行为数据（策略实例共享 + 数据预填）", carpCount == 2);
        Check("小鱼 × 2 条空策略空数据（不动只漂，也能生成）", bareCount == 2);

        // 全部出生在水域矩形内（左右 −500..1500、水面 0..400）——含 SpawnAtReady 在 SpawnPoint±范围随机后的位置
        bool inWater = true;
        foreach (var f in fishes)
        {
            var p = f.GlobalPosition;
            if (p.X < -500f || p.X > 1500f || p.Y < 0f || p.Y > 400f) inWater = false;
        }
        Check("全部鱼刷新位置在水域矩形内", inWater);

        // 深度区间：鲤鱼 y ∈ [70,150]（策略参数）；小鱼 y ∈ [20,200]（Unconfigured 默认）——刷新高度由行为参数决定
        bool depthOk = true;
        foreach (var f in fishes)
        {
            var isCarp = ReferenceEquals(f.BehaviorStrategy, _carpBehavior);
            var inRange = isCarp
                ? f.GlobalPosition.Y is >= 70f and <= 150f
                : f.GlobalPosition.Y is >= 20f and <= 200f;
            if (!inRange) depthOk = false;
        }
        Check("鱼 y 落在各自深度区间（刷新高度由行为参数决定）", depthOk);

        // 同种多条：PickAnchor 按 index 分槽 → 横向错开不重叠
        var carpXs = new HashSet<int>();
        foreach (var f in fishes)
        {
            if (ReferenceEquals(f.BehaviorStrategy, _carpBehavior)) carpXs.Add((int)Mathf.Round(f.GlobalPosition.X) / 50);
        }
        Check("同种多条鱼出生点横向错开（分布在不同槽位）", carpXs.Count >= 2);

        // ---- 3. 接线功能性验证：把场景 Hook 挪到一条鱼感应半径内 → 鱼感应到饵进入追逐 ----
        // 只验证接线（ConnectWater/ConnectBait 都生效了），不干扰上述位置断言
        var testee = fishes[0];
        _hook.GlobalPosition = testee.GlobalPosition + new Vector2(60, 0);   // 距 60 < 默认感应 130/150，且在水里
        testee.Step(1.0 / 60.0);
        Check("鱼感应到 Hook 上的饵 → 进入追逐（Water/BaitSource 接线生效）",
            testee.Current == Fish.State.Chase);

        // ---- 4. prefab：生成器挂 FishPrefab → 鱼带 AnimatedSprite2D + CollisionShape2D 结构（动画/碰撞） ----
        var prefab = ResourceLoader.Load<PackedScene>("res://Scenes/FishPrefab.tscn");
        Check("fish_prefab.tscn 可加载为 PackedScene", prefab != null);
        var prefabSpawner = new FishSpawner { Name = "PrefabSpawner", SpawnPool = new[] { carp }, CopiesPerDef = 1, FishPrefab = prefab };
        AddChild(prefabSpawner);
        prefabSpawner.ProcessMode = ProcessModeEnum.Disabled;
        var prefabFish = SpawnedFish(prefabSpawner);
        bool hasStructure = prefabFish.Count == 1
            && prefabFish[0].GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D") != null
            && prefabFish[0].GetNodeOrNull<CollisionShape2D>("CollisionShape2D") != null;
        Check("prefab 生成的鱼带 AnimatedSprite2D + CollisionShape2D（结构）", hasStructure);
        prefabSpawner.QueueFree();

        // ---- 5. 空池 + 目录有配置：回退扫 res://Data/Fishing/Fish/ 自动加载目录鱼（同 MainStage.LoadItemDefs 风格） ----
        var emptySpawner = new FishSpawner { Name = "EmptySpawner" };
        AddChild(emptySpawner);
        var fallback = SpawnedFish(emptySpawner);
        Check($"空池回退扫目录：自动加载 data/fishing/fish/ 里的鱼（实际 {fallback.Count} 条）", fallback.Count >= 1);
        Check("回退加载的鱼也都连好 Water/BaitSource（走默认接线）",
            fallback.TrueForAll(f => WaterRect().HasPoint(f.GlobalPosition)));

        spawner.QueueFree();
        emptySpawner.QueueFree();
        _water.QueueFree();
        _hook.QueueFree();
    }
}
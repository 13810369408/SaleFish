using System;
using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼类生成器：把配置好的一批鱼（FishDef）刷到主关卡水域里，让钓鱼目标活起来。
///
/// 配置（检查器，场景里挂一个 FishSpawner 节点即可用）：
///  ▪ SpawnPool —— 要生成的鱼种列表（FishDef 数组，自由增删，想放哪些鱼就拖哪些）；
///     为空时回退扫 res://Data/Fishing/Fish/ 目录加载全部配置文件，目录也没有则提示不生成；
///  ▪ CopiesPerDef —— 每种鱼生成几条（默认 1；生成 N 条会在水位里各自随机一个出生锚点）。
///  ▪ Water —— 水域（Area2D，深度/水面从它的矩形碰撞体几何读取）；留空自动在父节点下找名为 "Water" 的节点；
///  ▪ BaitSource —— 饵的载体（场景里的 Hook）；留空自动在父节点下找名为 "Hook" 的节点；
///  ▪ FishPrefab —— 鱼的 prefab（PackedScene，根挂 Fish.cs，带 AnimatedSprite2D + CollisionShape2D）；
///     留空回退代码 new Fish()（无动画纯逻辑，供测试）。
///
/// 每条鱼的行为来自 FishDef.BehaviorStrategy（策略；null = 鱼不动只漂着，生成时打一条警告）；
/// 内置 ClassicBehavior 的行为参数（巡逻矩形/深度等几何）直接写在策略实例上，生成器从它读取；出生锚点放在
/// Water 矩形内（y 落在水面下深度区间），再经 Fish.SpawnAtRandom 在「锚点 ± 巡逻范围 × 深度区间」
/// 里随机出真正的刷新位置——还是由行为数据决定。鱼刚生成就自己动（Fish._PhysicsProcess），
/// 自动测试要把生成器 ProcessMode 设 Disabled 或用真实场景手测。
/// </summary>
public sealed partial class FishSpawner : Node
{
    /// <summary>要生成的鱼种（检查器自由定义「生成哪些鱼」）；空 = 扫 res://Data/Fishing/Fish/ 目录回退。</summary>
    [Export] public FishDef[] SpawnPool = [];

    /// <summary>每种鱼生成几条（默认 1 条）。</summary>
    [Export] public int CopiesPerDef = 1;

    /// <summary>水域：Surface/深度由它推导；null 时自动在父节点下找 "Water"。</summary>
    [Export] public Area2D? Water;

    /// <summary>饵的载体（场景里的 Hook）；null 时自动在父节点下找 "Hook"。鱼靠它感应饵/咬钩。</summary>
    [Export] public Node2D? BaitSource;

    /// <summary>鱼的 prefab（PackedScene，根挂 Fish.cs，带 AnimatedSprite2D + CollisionShape2D）。
    /// null = 回退代码 new Fish()（无动画纯逻辑，供测试）。</summary>
    [Export] public PackedScene? FishPrefab;

    /// <summary>有鱼咬中（进入 Bitten）时触发：主关卡从这里接上钩逻辑（把鱼接上钩子）。</summary>
    public event Action<Fish>? FishBitten;

    private Area2D? _water;
    private Node2D? _bait;
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        if (GetParent() is Node parent)
        {
            _water ??= parent.GetNodeOrNull<Area2D>("Water");
            _bait ??= parent.GetNodeOrNull<Node2D>("Hook");
        }

        var defs = SpawnPool.Length > 0 ? [.. SpawnPool] : LoadDefsFromFolder();
        if (defs.Count == 0)
        {
            GD.PushWarning("[FishSpawner] SpawnPool 为空且 res://Data/Fishing/Fish/ 无配置：本场景未生成任何鱼。");
            return;
        }

        var copies = Mathf.Max(1, CopiesPerDef);
        foreach (var def in defs)
        {
            if (def == null) continue;
            for (int i = 0; i < copies; i++) SpawnOne(def, i);
        }
    }

    /// <summary>生成并接线一条鱼：策略取自 def.BehaviorStrategy、行为数据从内置 ClassicBehavior 的导出参数预填
    /// （自定义策略 → Unconfigured 兜底），接线（Water/BaitSource）在进树前完成——这样 _Ready 里的深度/水面判定
    /// 就用真实水位，而不是退化以部署点为水面；随后按行为数据就位。</summary>
    private void SpawnOne(FishDef def, int index)
    {
        // prefab 优先（带 AnimatedSprite2D + CollisionShape2D 的结构与视觉）；没挂 prefab 回退代码 new Fish()（纯逻辑）
        var fish = FishPrefab != null ? FishPrefab.Instantiate<Fish>() : new Fish();
        fish.Def = def;                            // 鱼种定义：上钩后的体力/耐力最大值/挣扎拖速、钓上后的背包形态 + 动画精灵表/播放速度
        fish.BehaviorStrategy = def.BehaviorStrategy;   // 平时行为策略：ClassicBehavior = 数据驱动巡逻/追饵；null = 不动只漂
        fish.BehaviorData = (def.BehaviorStrategy as ClassicBehavior)?.ToData() ?? FishBehaviorData.Unconfigured;   // 数据预填：巡逻矩形/深度等几何出生前就要用（ClassicBehavior 每帧也会把自己的参数同步回来）
        fish.Water = _water;          // _Ready 读导出字段：水面/深度从真实水域矩形取
        fish.BaitSource = _bait;      // 感应饵 + 咬钩判定的饵位置
        fish.GlobalPosition = PickAnchor(def, index);   // 先进树前就位：_Ready 把这里记为 SpawnPoint（巡逻/追逐/返回的锚）
        fish.Bitten += () => FishBitten?.Invoke(fish);   // 咬中 → 广播给主关卡（接上钩逻辑）
        AddChild(fish);

        if (fish.BehaviorData.PatrolDistanceX > 0f && !fish.SpawnAtReady)
            fish.SpawnAtRandom();   // 兜底：即使检查器关了 SpawnAtReady，也按行为数据站到巡逻范围内

        // 记录生成深度（图鉴探索记录用）：深度 = 就位后的 y − 水面（px，水面往下为正），
        // 存进 Fish.SpawnDepth 后固定不再变 —— 图鉴判定「这条鱼在哪个深度被见到」读它，
        // 而不是移动中的实时位置（避免追饵/收线偏移干扰玩家探索深度的判定）。
        var space = WaterBody.SpaceOf(_water);
        fish.SpawnDepth = space != null ? Mathf.Max(0f, fish.GlobalPosition.Y - space.SurfaceY) : 0f;
    }

    /// <summary>给一条鱼选出生锚点：x 在水域宽度内均匀错开（+小随机），y = 水面 + 深度区间内、
    /// 且不低于该 x 处的水底。Water 挂 WaterBody 时用湖床几何；普通矩形回退为矩形上下边。</summary>
    private Vector2 PickAnchor(FishDef def, int index)
    {
        var strategy = def.BehaviorStrategy;   // 深度声明已升到基类（图鉴同样读它）；自定义策略由它自己在 Update 里接管位置
        var depthMin = strategy?.SwimDepthMin ?? 20f;
        var depthMax = strategy?.SwimDepthMax ?? 200f;

        var space = WaterBody.SpaceOf(_water);
        if (space != null)
        {
            // 横向：到尾部分成 CopiesPerDef 份再各条内随机 ±60px，避免同种鱼叠在一起
            var slots = Mathf.Max(1, CopiesPerDef);
            var slot = ((float)index + 0.5f) / slots;
            var cx = Mathf.Lerp(space.XMin, space.XMax, slot);
            var x = Mathf.Clamp(cx + _rng.RandfRange(-60f, 60f), space.XMin, Mathf.Max(space.XMin, space.XMax - 1f));

            var yMin = space.SurfaceY + depthMin;
            var yMax = Mathf.Min(space.SurfaceY + depthMax, space.FloorYAt(x) - 1f);   // 不压在水底上
            var y = yMax > yMin ? _rng.RandfRange(yMin, yMax) : yMax;
            return new Vector2(x, y);
        }

        var rect = WaterRect();
        var top = rect?.Position.Y ?? 0f;
        if (rect.HasValue)
        {
            var slots = Mathf.Max(1, CopiesPerDef);
            var slot = ((float)index + 0.5f) / slots;                     // 0.5/slots .. (slots-0.5)/slots
            var cx = rect.Value.Position.X + rect.Value.Size.X * slot;    // 该条的中线
            var x = cx + _rng.RandfRange(-60f, 60f);
            var y = top + _rng.RandfRange(depthMin, depthMax);
            return new Vector2(x, y);
        }

        return new Vector2(_rng.RandfRange(-200, 200), top + _rng.RandfRange(depthMin, depthMax));
    }

    /// <summary>读取 res://Data/Fishing/Fish/ 目录下所有鱼配置（与 MainStage.LoadItemDefs 同风格）；目录缺失返回空列表。</summary>
    private static System.Collections.Generic.List<FishDef> LoadDefsFromFolder()
    {
        var list = new System.Collections.Generic.List<FishDef>();
        if (DirAccess.DirExistsAbsolute("res://Data/Fishing/Fish"))
        {
            var dir = DirAccess.Open("res://Data/Fishing/Fish");
            if (dir != null)
            {
                foreach (var file in dir.GetFiles())
                {
                    if (!file.EndsWith(".tres")) continue;
                    var def = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/" + file);
                    if (def != null) list.Add(def);
                }
            }
        }
        return list;
    }

    /// <summary>水区矩形（未旋转矩形碰撞体包围盒，与 Hook/Fish 同逻辑）。</summary>
    private Rect2? WaterRect()
    {
        if (_water == null || !GodotObject.IsInstanceValid(_water)) return null;
        if (_water.GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return null;
        if (cs.Shape is not RectangleShape2D rectShape || cs.Rotation != 0f) return null;
        var size = rectShape.Size * cs.GlobalScale;
        return new Rect2(cs.GlobalPosition - size / 2f, size);
    }
}
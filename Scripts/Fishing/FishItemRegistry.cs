using System.Collections.Generic;
using Godot;
using SaleFish.Inventory;

namespace SaleFish.Fishing;

/// <summary>
/// 「物品定义 → 鱼定义」注册表（静态，首次使用时建表）：
/// 仓库里存的是 <see cref="PlacementItemTypeDef"/>（物品），而卖鱼时要回答"这条鱼是什么鱼"
/// （取名字 / 标签 / 图鉴 Id），所以需要这张一对一映射。
///
/// 建表方式：扫描 <see cref="FishDir"/> 下所有 FishDef 资源，按 <c>FishDef.ItemDef</c> 的
/// <c>ResourcePath</c> 建键 —— 用路径字符串而不是对象实例，因为存档只存物品路径，
/// 读档后 ResourceLoader 重新加载出的是另一个对象实例，靠对象引用会查不到。
///
/// 约定：一条鱼对应一个物品定义（两条鱼共用一个物品定义 → 后者被忽略并告警）；
/// 查不到 = 该物品不是鱼（例如以后的其他贵重物），卖鱼时不参与价格板。
/// </summary>
public static class FishItemRegistry
{
    /// <summary>鱼定义所在目录（新增鱼资源放这里即可被自动收录）。</summary>
    public const string FishDir = "res://Data/Fishing/Fish";

    private static readonly Dictionary<string, FishDef> ByItemDefPath = [];
    private static readonly List<FishDef> AllFish = [];
    private static bool _built;

    /// <summary>全部鱼定义（建表顺序不保证；需要稳定顺序的地方自行排序）。</summary>
    public static IReadOnlyList<FishDef> All
    {
        get { EnsureBuilt(); return AllFish; }
    }

    /// <summary>物品定义 → 鱼定义；不是鱼返回 false。</summary>
    public static bool TryGetFish(PlacementItemTypeDef? itemDef, out FishDef fish)
    {
        fish = null!;
        if (itemDef == null) return false;
        var path = itemDef.ResourcePath;
        if (string.IsNullOrEmpty(path)) return false;   // 运行时临时拼的物品（无 .tres）不参与买卖
        EnsureBuilt();
        return ByItemDefPath.TryGetValue(path, out fish!);
    }

    /// <summary>物品实例 → 鱼定义（同 <see cref="TryGetFish(PlacementItemTypeDef?, out FishDef)"/>）。</summary>
    public static bool TryGetFish(InventoryItem? item, out FishDef fish) =>
        TryGetFish(item?.Data as PlacementItemTypeDef, out fish);

    /// <summary>测试/热重载用：清掉缓存，下次访问重新扫描。</summary>
    public static void Invalidate()
    {
        _built = false;
        ByItemDefPath.Clear();
        AllFish.Clear();
    }

    private static void EnsureBuilt()
    {
        if (_built) return;
        _built = true;
        using var dir = DirAccess.Open(FishDir);
        if (dir == null)
        {
            GD.PushWarning($"[FishItemRegistry] 打不开鱼定义目录 {FishDir}：卖鱼时找不到鱼信息。");
            return;
        }
        foreach (var file in dir.GetFiles())
        {
            if (!file.EndsWith(".tres")) continue;   // 导出后可能变成 .res；两种都收
            Register(ResourceLoader.Load<FishDef>($"{FishDir}/{file}"));
        }
        foreach (var file in dir.GetFiles())
        {
            if (!file.EndsWith(".res")) continue;
            Register(ResourceLoader.Load<FishDef>($"{FishDir}/{file}"));
        }
    }

    private static void Register(FishDef? fish)
    {
        if (fish == null) return;
        AllFish.Add(fish);
        var itemPath = fish.ItemDef?.ResourcePath;
        if (string.IsNullOrEmpty(itemPath))
        {
            GD.PushWarning($"[FishItemRegistry] 鱼「{fish.DisplayName}」(Id={fish.Id}) 没有配 ItemDef：无法入库/出售。");
            return;
        }
        if (ByItemDefPath.TryGetValue(itemPath, out var existing))
        {
            GD.PushWarning($"[FishItemRegistry] 物品定义 {itemPath} 同时属于「{existing.DisplayName}」和「{fish.DisplayName}」：后者被忽略（鱼与物品必须一一对应）。");
            return;
        }
        ByItemDefPath[itemPath] = fish;
    }
}

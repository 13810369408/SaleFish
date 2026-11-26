using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SaleFish.Save;

/// <summary>
/// 图鉴解锁日志（静态，跨场景存活）：记录每一种「见过」的鱼与其已探索的深度区间。
/// 三个事实：
///  ▪ 见过（场上有条目）→ 图鉴出现该鱼页（灰色剪影）；
///  ▪ 钓到过（Caught 置位）→ 该页变全彩、显示真名；
///  ▪ 深度区间 = 每次见到该鱼时「生成深度」的并集，只扩不缩 —— 玩家靠亲眼看逐渐逼近真实
///    深度区间（真实区间来自 FishDef.BehaviorStrategy.SwimDepthMin/Max，不直接公开给玩家）。
/// 深度数据源 = 鱼生成时就存好的 Fish.SpawnDepth，不是移动中的实时位置
/// （避免鱼追饵/收线导致的位置偏移干扰「探索到哪个深度」的判定）。
/// 与存档同命名空间：SaveManager 把这里快照进 SaveFile.Encyclopedia、读档时 Restore 回来。
/// 每次「实际有变化」才广播 Changed（每帧判定机场合不会刷屏）。
/// </summary>
public static class FishEncyclopediaLog
{
    private sealed class Entry
    {
        public float DepthMin;
        public float DepthMax;
        public bool Caught;
        /// <summary>历史最高成交价（0 = 还没卖过，图鉴显示"?"）。</summary>
        public int Price;
    }

    private static readonly Dictionary<long, Entry> Entries = [];

    /// <summary>任意条目变化（新增/区间扩展/钓获置位/整批恢复）时触发；UI（图鉴）订阅它刷新。</summary>
    public static event Action? Changed;

    /// <summary>是否见过该鱼（见过 = 图鉴有它的一页）。</summary>
    public static bool IsSeen(long fishId) => Entries.ContainsKey(fishId);

    /// <summary>是否钓到过（图鉴全彩）。</summary>
    public static bool IsCaught(long fishId) => Entries.TryGetValue(fishId, out var e) && e.Caught;

    /// <summary>已记录的深度区间；不存在返回 false（min/max 置 0）。</summary>
    public static bool TryGetDepthRange(long fishId, out float min, out float max)
    {
        if (Entries.TryGetValue(fishId, out var e)) { min = e.DepthMin; max = e.DepthMax; return true; }
        min = max = 0f;
        return false;
    }

    /// <summary>见到该鱼在 depth 深的位置：折进记录区间（只扩不缩，与顺序无关）；实际有变化才广播。</summary>
    public static void See(long fishId, float depth)
    {
        var d = Mathf.Max(0f, depth);
        if (Entries.TryGetValue(fishId, out var e))
        {
            if (!(d < e.DepthMin) && !(d > e.DepthMax)) return;   // 已覆盖，无变化
            e.DepthMin = Mathf.Min(e.DepthMin, d);
            e.DepthMax = Mathf.Max(e.DepthMax, d);
        }
        else
        {
            Entries[fishId] = new Entry { DepthMin = d, DepthMax = d };
        }
        Changed?.Invoke();
    }

    /// <summary>钓获该鱼：置位（并保证有条目——被钓到的鱼必然已见）。</summary>
    public static void Caught(long fishId)
    {
        if (Entries.TryGetValue(fishId, out var e))
        {
            if (e.Caught) return;
            e.Caught = true;
        }
        else
        {
            Entries[fishId] = new Entry { Caught = true };   // 理论不会发生，防御
        }
        Changed?.Invoke();
    }

    /// <summary>该鱼的历史最高成交价（图鉴价格行）：没卖过 / 无记录返回 false。</summary>
    public static bool TryGetPrice(long fishId, out int price)
    {
        if (Entries.TryGetValue(fishId, out var e) && e.Price > 0) { price = e.Price; return true; }
        price = 0;
        return false;
    }

    /// <summary>卖出一条鱼：把成交价记入图鉴（只往上更新 = 记录历史最高成交价；0 不计）。实际有变化才广播。</summary>
    public static void RecordSale(long fishId, int price)
    {
        if (price <= 0) return;
        if (Entries.TryGetValue(fishId, out var e))
        {
            if (price <= e.Price) return;
            e.Price = price;
        }
        else
        {
            Entries[fishId] = new Entry { Caught = true, Price = price };   // 理论不会发生，防御
        }
        Changed?.Invoke();
    }

    /// <summary>快照成存档结构（SaveNow 用）。</summary>
    public static List<EncyclopediaSave> Snapshot() =>
        [.. Entries.Select(kv => new EncyclopediaSave
        {
            FishId = kv.Key,
            DepthMin = kv.Value.DepthMin,
            DepthMax = kv.Value.DepthMax,
            Caught = kv.Value.Caught,
            Price = kv.Value.Price,
        })];

    /// <summary>整批替换为存档内容（读档用）；整批变化只广播一次（不逐条刷 UI）。</summary>
    public static void Restore(IEnumerable<EncyclopediaSave> records)
    {
        ClearAll();
        if (records == null) return;
        foreach (var r in records)
        {
            if (r == null) continue;
            Entries[r.FishId] = new Entry
            {
                DepthMin = r.DepthMin,
                DepthMax = r.DepthMax,
                Caught = r.Caught,
                Price = r.Price,
            };
        }
        if (Entries.Count > 0) Changed?.Invoke();
    }

    /// <summary>清空全部（NewGame / 测试）。</summary>
    public static void ClearAll()
    {
        if (Entries.Count == 0) return;
        Entries.Clear();
        Changed?.Invoke();
    }
}
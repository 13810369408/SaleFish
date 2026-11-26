using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.View;

namespace SaleFish.Shop;

/// <summary>
/// 卖鱼结算（纯逻辑，不碰 UI / 场景节点，可单测）。
///
/// 输入 = 一次"货架快照"：仓库里的每条鱼（物品实例 + 所属鱼定义 + 玩家标价 + 实际价值），
/// 加上一个 **seed**。输出 = 结算结果（每个顾客买了什么、总收入、每种鱼卖出/剩余条数）。
///
/// **确定性（防读档刷结果）**：seed 由「当前货架 + 定价」确定性派生（<see cref="DeriveSeed"/>），
/// 同样的货、同样的价 → 永远同样的顾客、预算、折扣判定。配合存档里再记一份 seed，
/// 读档重算的结果与第一次完全一致，玩家无法靠反复读档刷出更好的结果。
///
/// 顾客规则（与需求一致）：
///  ▪ 到店顾客数 = rand(MinCustomerCount, MaxCustomerCount)；种类从顾客池随机；预算 = rand(MinBudget, MaxBudget)；
///  ▪ 每个顾客先把当前所有鱼**打乱顺序**，再逐个判断，直到没钱；
///  ▪ 命中偏好：标价 ≤ 实际价值 → 买；标价 &gt; 实际价值 → 不买；
///  ▪ 未命中偏好：标价 &lt; 0.7×实际价值时，按 ((0.7×A − P) / (0.7×A))² 的概率想买（每条鱼各掷一次）；
///  ▪ A ≤ 0（忘填价值）→ 折扣概率 0，安全兜底。
/// </summary>
public static class SellService
{
    /// <summary>货架上的一条鱼（一次实例，对应仓库里的一件物品）。</summary>
    public sealed class Lot
    {
        /// <summary>物品实例（结算后按它从仓库里移除）。</summary>
        public InventoryItem Item = null!;
        /// <summary>物品所在的仓库视图（移除用；可能为 null = 不在任何仓库里）。</summary>
        public InventoryView? View;
        /// <summary>对应的鱼定义（名字/标签/图鉴 Id）。</summary>
        public FishDef Fish = null!;
        /// <summary>玩家标价（价格板填的，按鱼种）。</summary>
        public int Price;
        /// <summary>实际价值（物品定义上的 SellPrice）。</summary>
        public int Value;
    }

    /// <summary>一个顾客的购买结果。</summary>
    public sealed class CustomerResult
    {
        public CustomerDef Def = null!;
        /// <summary>到店预算（身上带的钱）。</summary>
        public int Budget;
        /// <summary>实际花掉的钱。</summary>
        public int Spent;
        /// <summary>买走的鱼（按购买顺序；同一条鱼不会重复）。</summary>
        public readonly List<Lot> Bought = [];
        /// <summary>剩余预算（= Budget − Spent）。</summary>
        public int Left => Budget - Spent;
    }

    /// <summary>整场结算结果。</summary>
    public sealed class Result
    {
        /// <summary>本次使用的种子（确定性重算用；也会写进存档）。</summary>
        public long Seed;
        public readonly List<CustomerResult> Customers = [];
        /// <summary>被买走的鱼（用于从仓库移除 + 更新图鉴）。</summary>
        public readonly List<Lot> Sold = [];
        /// <summary>总收入。</summary>
        public int TotalIncome;
        /// <summary>每种鱼的成交条数（FishDef.Id → 条数）。</summary>
        public readonly Dictionary<long, int> SoldCount = [];
        /// <summary>每种鱼的成交额（FishDef.Id → 总价），图鉴取最高单条成交价时用。</summary>
        public readonly Dictionary<long, int> SoldRevenue = [];
        /// <summary>每种鱼的最高单条成交价（FishDef.Id → 最高价），图鉴记录用。</summary>
        public readonly Dictionary<long, int> BestPrice = [];
        /// <summary>每种鱼的剩余条数（FishDef.Id → 条数，含没卖掉的）。</summary>
        public readonly Dictionary<long, int> RemainingCount = [];
    }

    /// <summary>
    /// 由「货架 + 定价」派生确定性种子（FNV-1a 64）。相同货架 + 相同定价 → 相同种子 → 相同结算。
    /// 不依赖时间/随机数，所以卖前存档再读档重卖，结果也完全一样。
    /// </summary>
    public static long DeriveSeed(IEnumerable<Lot> lots)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var lot in lots.OrderBy(l => l.Fish.Id).ThenBy(l => l.Price))
        {
            hash = Mix(hash, (ulong)lot.Fish.Id);
            hash = Mix(hash, (ulong)(uint)lot.Price);
            hash = Mix(hash, (ulong)(uint)lot.Value);
        }
        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);   // 非负，避免 RandomNumberGenerator 取负
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
            return hash;
        }
    }

    /// <summary>
    /// 跑一次结算。
    /// </summary>
    /// <param name="lots">货架上的所有鱼（仓库里的）。</param>
    /// <param name="customers">顾客池（至少一个）。</param>
    /// <param name="minCustomers"/><param name="maxCustomers">到店人数范围（含）。</param>
    /// <param name="seed">种子（见 <see cref="DeriveSeed"/>；调用方可传存档里的旧值）。</param>
    public static Result Run(IReadOnlyList<Lot> lots, IReadOnlyList<CustomerDef> customers,
                             int minCustomers, int maxCustomers, long seed)
    {
        var result = new Result { Seed = seed };
        foreach (var lot in lots)
        {
            result.RemainingCount.TryGetValue(lot.Fish.Id, out var n);
            result.RemainingCount[lot.Fish.Id] = n + 1;
        }
        if (customers.Count == 0) return result;

        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        var lo = Mathf.Max(0, Mathf.Min(minCustomers, maxCustomers));
        var hi = Mathf.Max(lo, Mathf.Max(minCustomers, maxCustomers));
        var count = lo == hi ? lo : (int)rng.RandiRange(lo, hi);

        // 每个顾客独立打乱顺序（打乱在顾客循环内做，互不影响）
        var available = lots.ToList();
        for (var i = 0; i < count && available.Count > 0; i++)
        {
            var def = customers[(int)rng.RandiRange(0, customers.Count - 1)];
            var budget = def.MaxBudget <= def.MinBudget
                ? def.MinBudget
                : (int)rng.RandiRange(def.MinBudget, def.MaxBudget);
            var customer = new CustomerResult { Def = def, Budget = budget };

            // 打乱这份货（Fisher–Yates，用同一个确定性 RNG）
            var shuffled = available.ToList();
            for (var k = shuffled.Count - 1; k > 0; k--)
            {
                var j = (int)rng.RandiRange(0, k);
                (shuffled[k], shuffled[j]) = (shuffled[j], shuffled[k]);
            }

            foreach (var lot in shuffled)
            {
                if (customer.Left < lot.Price) continue;          // 买不起
                if (!WantsToBuy(rng, lot, customer.Def)) continue; // 不想买（偏好/折扣判定）
                customer.Bought.Add(lot);
                customer.Spent += lot.Price;
                available.Remove(lot);
            }
            result.Customers.Add(customer);   // 空手顾客也记录（详情里显示"没买"）
        }

        // 汇总
        foreach (var c in result.Customers)
        {
            foreach (var lot in c.Bought)
            {
                result.Sold.Add(lot);
                result.TotalIncome += lot.Price;
                result.SoldCount.TryGetValue(lot.Fish.Id, out var n);
                result.SoldCount[lot.Fish.Id] = n + 1;
                result.SoldRevenue.TryGetValue(lot.Fish.Id, out var r);
                result.SoldRevenue[lot.Fish.Id] = r + lot.Price;
                result.BestPrice.TryGetValue(lot.Fish.Id, out var best);
                if (lot.Price > best) result.BestPrice[lot.Fish.Id] = lot.Price;
                result.RemainingCount[lot.Fish.Id] = result.RemainingCount[lot.Fish.Id] - 1;
            }
        }
        return result;
    }

    /// <summary>这个顾客是否想买这条鱼（偏好优先，其次折扣概率）。</summary>
    private static bool WantsToBuy(RandomNumberGenerator rng, Lot lot, CustomerDef customer)
    {
        var preferred = lot.Fish.Tags.Any(t => customer.PreferenceTags.Contains(t));
        if (preferred) return lot.Price <= lot.Value;   // 标价高于实际价值：偏好顾客也不买
        var p = DiscountChance(lot.Price, lot.Value);
        return p > 0f && rng.Randf() < p;
    }

    /// <summary>
    /// 折扣吸引概率：((0.7×A − P) / (0.7×A))²，P ≥ 0.7A 或 A ≤ 0 → 0；P = 0 → 1。
    /// 标价正好等于 70% 实际价值时概率为 0；价格为 0 时概率 100%。
    /// </summary>
    public static float DiscountChance(int price, int value)
    {
        if (value <= 0) return 0f;
        var threshold = 0.7f * value;
        if (threshold <= 0f) return 0f;
        var ratio = (threshold - price) / threshold;
        if (ratio <= 0f) return 0f;
        return ratio * ratio;
    }
}

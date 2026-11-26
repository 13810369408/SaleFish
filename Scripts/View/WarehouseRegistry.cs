using System.Collections.Generic;
using Godot;

namespace SaleFish.View;

/// <summary>
/// 在用仓库登记表：让「指针命中/顶层判定」在仓库间统一。
/// 每座仓库（InventoryView）Setup 时登记、离场（_ExitTree）时注销；显示（Show）重新登记、隐藏（Hide）注销。
/// 用途：
///  ▪ TopmostAt —— 判定指针所在的最上层仓库（后登记视为更上层），只有它绘制手持本体与幽灵预览；
///  ▪ LastView —— 指针下方没有仓库时兜底取「最近登记的仓库」做参考（生成物品的默认画布等）。
/// 手持物品本身是全局事实，由 autoload 的 HeldItemController 持有，不在这里。
/// </summary>
public static class WarehouseRegistry
{
    private static readonly List<InventoryView> Views = [];

    public static void Register(InventoryView view)
    {
        if (view == null || !GodotObject.IsInstanceValid(view)) return;
        Views.Remove(view);
        Views.Add(view);   // 移到末尾 = 更晚入树 = 绘制在上层，指针命中优先
    }

    public static void Unregister(InventoryView view)
    {
        Views.Remove(view);
    }

    /// <summary>指针所在的最上层仓库（后登记=上层）；指针不在任何仓库上返回 null。</summary>
    public static InventoryView? TopmostAt(Vector2 globalPos)
    {
        for (var i = Views.Count - 1; i >= 0; i--)
        {
            var v = Views[i];
            if (!GodotObject.IsInstanceValid(v)) { Views.RemoveAt(i); continue; }
            if (v.GetGlobalRect().HasPoint(globalPos)) return v;
        }
        return null;
    }

    /// <summary>最近登记的可用仓库（兜底参考，跨场景取活实例）；仓库表为空返回 null。</summary>
    public static InventoryView? LastView()
    {
        for (var i = Views.Count - 1; i >= 0; i--)
        {
            var v = Views[i];
            if (!GodotObject.IsInstanceValid(v)) { Views.RemoveAt(i); continue; }
            return v;
        }
        return null;
    }

    /// <summary>当前所有「打开中」的仓库视图（可见且有效，新→旧）；供宿主枚举/定向关闭等使用。</summary>
    public static List<InventoryView> OpenViews()
    {
        var list = new List<InventoryView>();
        for (var i = Views.Count - 1; i >= 0; i--)
        {
            var v = Views[i];
            if (!GodotObject.IsInstanceValid(v)) { Views.RemoveAt(i); continue; }
            if (v.Visible) list.Add(v);
        }
        return list;
    }

    /// <summary>唯一打开中的仓库视图：恰有一座打开时返回它（供「外部点击关闭当前仓库」用），
    /// 0 座或多座同时打开返回 null。</summary>
    public static InventoryView? SingleOpen()
    {
        InventoryView? single = null;
        for (var i = Views.Count - 1; i >= 0; i--)
        {
            var v = Views[i];
            if (!GodotObject.IsInstanceValid(v)) { Views.RemoveAt(i); continue; }
            if (!v.Visible) continue;
            if (single != null) return null;   // ≥2 座
            single = v;
        }
        return single;
    }

    /// <summary>当前「打开中」的仓库数量（可见且在用的登记视图）：Setup/Show 登记、Hide/离场注销，
    /// 因此本表即代表打开状态——0 = 无仓库打开（外界点击走正常流程）；1 = 单仓库（外部点击可关闭当前仓库）；
    /// ≥2 = 多仓库同时打开（此时外部点击一律不生效，仅各自的开/关按钮可隐藏所对应的仓库）。
    /// 同样顺手清理失效登记（与 TopmostAt/LastView 同款防御）。</summary>
    public static int OpenCount
    {
        get
        {
            var count = 0;
            for (var i = Views.Count - 1; i >= 0; i--)
            {
                var v = Views[i];
                if (!GodotObject.IsInstanceValid(v)) { Views.RemoveAt(i); continue; }
                if (v.Visible) count++;
            }
            return count;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SaleFish.Shop;

/// <summary>
/// 顾客池：启动时扫描 <see cref="CustomerDir"/> 下所有 CustomerDef 资源。
/// 卖鱼时从这里随机抽人来买（种类 + 预算都走确定性随机，见 SellService）。
/// </summary>
public static class CustomerRegistry
{
    /// <summary>顾客定义所在目录（新增顾客放这里即可被自动收录）。</summary>
    public const string CustomerDir = "res://Data/Shop/Customers";

    private static List<CustomerDef>? _all;

    /// <summary>全部顾客定义（按文件名排序，保证同一批资源顺序稳定）。</summary>
    public static IReadOnlyList<CustomerDef> All
    {
        get
        {
            if (_all != null) return _all;
            _all = [];
            using var dir = DirAccess.Open(CustomerDir);
            if (dir == null)
            {
                GD.PushWarning($"[CustomerRegistry] 打不开顾客目录 {CustomerDir}：卖鱼时不会有顾客。");
                return _all;
            }
            var files = dir.GetFiles().Where(f => f.EndsWith(".tres") || f.EndsWith(".res")).ToList();
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var def = ResourceLoader.Load<CustomerDef>($"{CustomerDir}/{file}");
                if (def != null) _all.Add(def);
            }
            return _all;
        }
    }

    /// <summary>测试/热重载用：清缓存。</summary>
    public static void Invalidate() => _all = null;
}

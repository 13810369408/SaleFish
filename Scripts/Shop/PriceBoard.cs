using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SaleFish.Shop;

/// <summary>
/// 价格板（世界里的可点击精灵，挂 <see cref="Sprite2D"/> 脚本）：点击 → 宿主（MainStage）打开价格面板。
/// 与 WarehouseButton 同款模式：组件只负责「命中测试 + 报告被点击」，输入路由和 UI 都归宿主。
/// </summary>
[GlobalClass]
public sealed partial class PriceBoard : Sprite2D
{
    private static readonly List<PriceBoard> All = [];

    /// <summary>被左键点击（宿主路由到命中本节点时调用 <see cref="RaiseClicked"/>）。</summary>
    public event Action? Clicked;

    public override void _Ready()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    public override void _ExitTree()
    {
        All.Remove(this);
    }

    /// <summary>宿主输入路由用：画布坐标是否命中了某块价格板；未命中返回 null。</summary>
    public static PriceBoard? HitTest(Vector2 canvasPoint)
    {
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var b = All[i];
            if (!GodotObject.IsInstanceValid(b)) { All.RemoveAt(i); continue; }
            if (b.Texture == null) continue;
            if (b.GetRect().HasPoint(b.ToLocal(canvasPoint))) return b;
        }
        return null;
    }

    /// <summary>报告点击（宿主在命中后调用）。</summary>
    public void RaiseClicked() => Clicked?.Invoke();
}

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

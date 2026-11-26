using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼钩定义（局外配置，Godot Resource）：一种鱼钩的参数——贴图 + 默认重量。
/// 默认重量 = 无配重时的钩子重量（沿用 Hook.Weight 的基准 1f）；挂配重后
/// 总重量 = 本默认重量 + 配重重量（叠加）。
///
/// 本类只定义「这种钩子是什么」；「哪种钩子能钓到哪种鱼」由的 FishDef.Hooks 数组声明。
/// 在 `res://Data/Fishing/Hooks/` 下新建 Resource 并选本类即可配置一种鱼钩。
/// </summary>
[GlobalClass]
public sealed partial class FishingHookDef : Resource
{
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名。</summary>
    [Export] public string DisplayName = "";

    /// <summary>贴图（暂无则回退占位）。</summary>
    [Export] public Texture2D? Texture;

    /// <summary>默认重量（无配重时钩子本身的重量；基准 1 = 标准钩，沿用 Hook.Weight 默认。
    /// 挂配重时总重量 = 此默认重量 + 配重重量）。</summary>
    [Export] public float Weight = 1f;
}
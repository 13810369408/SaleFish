using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼饵定义（局外配置，Godot Resource）：一种真饵的参数——目前只有贴图。
///
/// 注意与「饵鱼」区分：鱼作为鱼饵是另一条路（装进 Hook.LiveBait，匹配看目标鱼的 LiveBaitTags 与本鱼 Tags 的交集），
/// 本类是普通商品饵（装进 Hook.Bait、由目标鱼的 Baits[] 声明）。
/// 在 `res://Data/Fishing/Baits/` 下新建 Resource 并选本类即可配置一种鱼饵。
/// </summary>
[GlobalClass]
public sealed partial class BaitDef : Resource
{
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名。</summary>
    [Export] public string DisplayName = "";

    /// <summary>贴图（暂无则回退占位）。</summary>
    [Export] public Texture2D? Texture;
}
using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼标签定义（局外配置，Godot Resource）：一个纯数据标记，用来给鱼分类，替代「引用具体某条鱼」的硬关联。
///
/// 用法：一条鱼自己的属性用 FishDef.Tags 挂上若干标签（例：沙丁鱼 Tags = [超小型]）；
/// 捕食者的「会咬哪些活饵」用 FishDef.LiveBaitTags 挂上标签（例：海鲈鱼 LiveBaitTags = [超小型]）。
/// 用某条鱼当饵鱼时，看它的 Tags 与目标鱼的 LiveBaitTags 有没有交集，命中即咬。
/// 标签不携带任何行为（那是 Behavior / Struggle 策略类的事），只是可复用、可批量改名的事实分类。
/// 在 `res://Data/Fishing/Tags/` 下新建 Resource 并选本类即可配置一个标签。
/// </summary>
[GlobalClass]
public sealed partial class FishTagDef : Resource
{
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名（标签名，如「超小型」）。</summary>
    [Export] public string DisplayName = "";
}

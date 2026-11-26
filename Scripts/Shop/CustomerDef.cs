using Godot;
using SaleFish.Fishing;

namespace SaleFish.Shop;

/// <summary>
/// 顾客定义（局外配置，Godot Resource）：卖鱼时随机到店的一类客人。
///  ▪ <see cref="PreferenceTags"/>：偏好标签。仓库里的鱼只要有任一标签命中，这类顾客就会考虑购买；
///  ▪ <see cref="MinBudget"/>/<see cref="MaxBudget"/>：本类顾客到店时携带的钱（两者之间随机）。
///
/// 鱼与偏好都用「标签」对齐（FishDef.Tags ∩ PreferenceTags ≠ ∅），不写死具体某条鱼，
/// 以后加鱼只要挂上对应标签就自动进入对应顾客的购物清单。
/// 在 `res://Data/Shop/Customers/` 下新建 Resource 并选本类即可配置一类顾客（启动时自动扫描）。
/// </summary>
[GlobalClass]
public sealed partial class CustomerDef : Resource
{
    /// <summary>唯一 Id（结算详情/测试用）。</summary>
    [Export] public long Id;

    /// <summary>显示名（如「厨师」）。</summary>
    [Export] public string DisplayName = "";

    /// <summary>偏好标签：与鱼的 Tags 有交集 = 这类顾客会看这条鱼。</summary>
    [Export] public FishTagDef[] PreferenceTags = [];

    /// <summary>到店随机预算下限（含）。</summary>
    [Export] public int MinBudget = 200;

    /// <summary>到店随机预算上限（含）。</summary>
    [Export] public int MaxBudget = 600;
}

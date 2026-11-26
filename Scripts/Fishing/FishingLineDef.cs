using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼线定义（局外配置，Godot Resource）：一根鱼线的强度与长度。
///  ▪ MaxTension —— 最大拉力值：挣扎中按住左键施力时拉力上涨，达到该值 → 鱼脱钩；
///  ▪ Length —— 线长（px）：挣扎时鱼把钩拉走的最远距离（拉到线长 → 直接脱钩），
///    同时限制抛出最远距离与"未收线时能沉到的最深深度"（钩子到抛出起点的距离不能超过线长）。
/// 在 res://Data/Fishing/Lines/ 下新建 Resource 并选本类即可配置一种鱼线（主关卡水上钩前可换）。
/// </summary>
[GlobalClass]
public sealed partial class FishingLineDef : Resource
{
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名（鱼线名）。</summary>
    [Export] public string DisplayName = "";

    /// <summary>最大拉力值：挣扎中按住左键拉线时拉力上升，达到该值 → 鱼脱钩（拉力保不住鱼就放它走）。</summary>
    [Export] public float MaxTension = 100f;

    /// <summary>线长（px）：钩子到抛出起点的最远距离（抛出/下沉/挣扎拉走都不能超过它）。</summary>
    [Export] public float Length = 1000f;
}
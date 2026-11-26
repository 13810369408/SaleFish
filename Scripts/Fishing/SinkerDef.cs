using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 配重定义（局外配置，Godot Resource）：一种配重的参数——贴图 + 重量。
///
/// 重量语义 = **叠加**：挂上配重后总重量 = 钩子默认重量（FishingHookDef.Weight）+ 配重重量
/// （沿用 √重量 曲线，越重水中下沉越快、漂停越慢）。配重是「额外加的分量」，不是替换钩子重量。
/// 在 `res://Data/Fishing/Sinkers/` 下新建 Resource 并选本类即可配置一种配重。
/// </summary>
[GlobalClass]
public sealed partial class SinkerDef : Resource
{
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名。</summary>
    [Export] public string DisplayName = "";

    /// <summary>贴图（暂无则回退占位）。</summary>
    [Export] public Texture2D? Texture;

    /// <summary>配重重量：挂上后与钩子默认重量相加（叠加，不替换）。</summary>
    [Export] public float Weight = 1f;
}
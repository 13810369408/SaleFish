using System;
using Godot;

namespace SaleFish.Inventory;

/// <summary>
/// 放置物类型定义：局外可配置（Godot Resource，可在检查器里新建/编辑，等价 Unity 的 ScriptableObject）。
///
/// 字段分两类：
///  ▪ 逻辑字段（显示名 / 占格形状）：数据层消费，ToShape / ToItem 在此转换；
///  ▪ 显示字段（Texture / DisplaySize / DrawOffset）：仅渲染层（InventoryView）读取，逻辑层绝不解读。
///
/// 显示约定：贴图整幅显示、不按格切片；无贴图时该物品统一渲染为白色占位。
///   显示尺寸 = DisplaySize × 格大小（DisplaySize 单位=格，即「显示占多少格」；某轴 0 = 用该轴形状格数）
///   显示位置 = 形状锚点 + DrawOffset × 格大小（DrawOffset 单位=格 → 跟随格子缩放）
/// </summary>
/// <example>
/// 在 `res://Data/Inventory/Items/` 下新建 Resource 并选择本类即可配置一个物品；
/// 代码里也可直接 new：<see cref="PlacementItemTypeDef"/> { ShapePattern = new[] { "XXX", ".X." } } 即 T 形。
/// </example>
[GlobalClass]
public sealed partial class PlacementItemTypeDef : Resource
{
    /// <summary>显示名。</summary>
    [Export] public string DisplayName = "";

    /// <summary>售价（实际价值）：卖鱼时顾客用它判断「玩家标价是否不高于实际价值」，也是打折概率曲线
    /// ((0.7×本值 − 标价) / (0.7×本值))² 的基准。非鱼物品也可以填，方便以后出现其他可卖物。</summary>
    [Export] public int SellPrice;

    /// <summary>占格形状图案：每个字符串一行，'X'/'x'/'#' 为占用格，其余为空白。
    /// 例：T 形 = { "XXX", ".X." }，2×2 方块 = { "XX", "XX" }。
    /// </summary>
    [Export] public string[] ShapePattern = Array.Empty<string>();

    // ---- 显示字段：只读给视图（InventoryView），逻辑层不触碰 ----

    /// <summary>整张贴图（不切格），整件物品绘制为一幅完整图片；为空则回退到占位色色块。</summary>
    [Export] public Texture2D? Texture;

    /// <summary>显示尺寸（单位=格）：整幅贴图/白色占位实际占的格数宽高。显示像素尺寸 = 格大小 × DisplaySize。
    /// 某轴为 0 = 用该轴的「形状格数」（默认跟随形状，旧行为）；想让它显示成别的大小直接填格数，如 (4,2)。</summary>
    [Export] public Vector2 DisplaySize = Vector2.Zero;

    /// <summary>贴图相对形状包围盒的偏移，单位=格（像素 = 值 × 格大小，换任何格大小的仓库都跟随缩放）。</summary>
    [Export] public Vector2 DrawOffset;

    // ---- 初始抓取锚点：仅输入层（全局手 HeldItemController）在「生成」（TakeExternal）时读取 ----

    /// <summary>
    /// 初始抓取锚点——形状内哪一格作为抓手，单位=格，相对形状左上角；(-1,-1) = 未定义 → 生成时自动居中。
    /// 配合 InitialSnap 定锚点的具体位置（中心/边中点/角）。只在「生成」时生效：
    /// 放进仓库后再点击拾取，仍按现有「点击吸附最近锚点」逻辑，不受此字段影响。
    /// </summary>
    [Export] public Vector2I InitialGrabCell = new(-1, -1);

    /// <summary>
    /// 初始锚点在抓手格内的 0..1 比例：0.5=格中心；0/1 = 格边缘。
    /// 例：(0,0.5)/(1,0.5)=上下边中点；(0.5,0)/(0.5,1)=左右边中点；四角=(0,0)(0,1)(1,0)(1,1)。
    /// 合法性规则与拾取吸附一致：中心恒可用；边中点需为形状内格子共用边；角需 ≥3 格共用。
    /// 非法（或非 0/0.5/1 的中间值）自动回退到该格最近合法锚点。
    /// </summary>
    [Export] public Vector2 InitialSnap = new(0.5f, 0.5f);

    /// <summary>转换为数据层占格形状（形状数学与图片无关，图片永远整幅显示）。</summary>
    public PlacementShape ToShape() => PlacementShape.FromPattern(ShapePattern);

    /// <summary>转换为一个可放入背包的物品实例；Data 携带本定义，供渲染层取显示信息。
    /// 无贴图时该物品在背包/手中渲染为白色占位（统一规则）。</summary>
    public InventoryItem ToItem() => new(ToShape(), this);
}
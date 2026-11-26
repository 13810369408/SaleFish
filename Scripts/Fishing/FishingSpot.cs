using Godot;
using SaleFish.Common;

namespace SaleFish.Fishing;

/// <summary>
/// 钓鱼点（场景 FishingSpot，Node2D）：一个可交互的「坐下钓鱼」落点。
/// 几何与高亮全部来自 <see cref="InteractZone"/>（Sprite + Detect + Highlight）；
/// 输入 / 状态机留在 MainStage 的 FreeMove 阶段：玩家进检测区 → 高亮；鼠标悬停 → 「坐下钓鱼」提示；
/// 左键命中 → 玩家以行走速度走到本点 X 再切回 Ready。
/// 保留独立类型只为语义清晰（与鱼店 FishShop 等共用同一基类）。
/// </summary>
[GlobalClass]
public sealed partial class FishingSpot : InteractZone
{
}

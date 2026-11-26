using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼平时行为策略（FishDef.BehaviorStrategy 槽里填的抽象基类）：每种鱼/每种行为一个派生类，
/// 平时巡逻/追饵/返回的每帧决策由它完全接管着写。
///
/// 用法（给一种新鱼写专属行为）：
///  ▪ 在 C# 里建一个 [GlobalClass] 派生类（如 `public sealed partial class SharkBehavior : FishBehaviorBase`），
///    需要的参数用 [Export] 字段画到检查器；想沿用数据驱动巡逻/追饵/返回直接挂内置的 ClassicBehavior；
///  ▪ 在 FishDef 的 BehaviorStrategy 槽「快速载入→新建」选这个类，填参数；
///  ▪ BehaviorStrategy = null = 这条鱼不动，只在水里漂着，生成时打一条仅测试可见的警告。
///
/// 每帧由 Fish.Step 调用 Update。决策用 ctx（FishContext）读到本帧鱼的只读状态，
/// 用它的操作原语（MoveToward/PickNextTarget/SetState…）下令移动——「走路」机制
/// （转向翻转、巡逻矩形钳制）始终留在 Fish 内部，任何行为都共用同一套规则。
/// 水体几何也经 ctx 读：DistanceToSurface / DistanceToBottom / DistanceToLeft / DistanceToRight / IsInWater
/// （都不扣身体半径；余量由策略自己判断，例如「离水面 &lt; 100 就转向」——基类和 Fish 都不内置余量）。
/// 不需要表态的帧直接 return（鱼保持现状）。
/// 策略默认被同一至多份 FishDef 共享：无状态（每鱼状态放 ctx 或 Fish 上）才能安全复用文件实例。
/// </summary>
[GlobalClass]
public abstract partial class FishBehaviorBase : Resource
{
    /// <summary>游动深度区间（下限，相对水面，px；水面往下为正）。放在基类：
    /// 图鉴（未公开给玩家）与生成器都要读它，任何行为实现都携带这一对「深度声明」。</summary>
    [Export] public float SwimDepthMin = 20f;

    /// <summary>游动深度区间（上限，相对水面，px）。刷新位置的 y 在这个区间里随机。</summary>
    [Export] public float SwimDepthMax = 200f;

    /// <summary>
    /// 平时行为每帧决策入口（鱼不处于 Bitten 时每帧调用；咬中后由钓上逻辑接管）。
    /// ctx = 本帧鱼的只读快照 + 决策原语；delta = 本帧秒数。
    /// 想用数据驱动的内置巡逻/追饵/返回：把行为数据流进 ctx.BehaviorData
    /// （内置 ClassicBehavior 就是这样每帧把自己的导出参数打包进 ctx.BehaviorData），
    /// 再用 ctx 的状态机原语（SetState/MoveToward/PickNextTarget/EnterBitten）写自己的决策。
    /// </summary>
    public abstract void Update(FishContext ctx, double delta);
}
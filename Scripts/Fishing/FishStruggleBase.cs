using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 上钩挣扎策略（FishDef.Struggle 槽里填的抽象基类）：每种鱼/每种挣扎一个派生类，
/// 上钩后的每帧决策由它完全接管着写。
///
/// 用法：
///  ▪ 在 C# 里建一个 [GlobalClass] 派生类（如 `public sealed partial class SharkStruggle : FishStruggleBase`），
///    参数用 [Export] 字段画到检查器；想复用体力/耐力/拉力数值机直接挂内置的 ClassicStruggle；
///  ▪ 在 FishDef 的 Struggle 槽「快速载入→新建」选这个类；
///  ▪ Struggle = null = 上钩后安静待收线（不拖钩子，玩家可直接收线拉回），上钩时打一条仅测试可见的警告。
///
/// 规则边界（Hook 负责的「机械层」，不交给鱼）：
///  ▪ 线长钳制 / 拉到线长极限 → 自动脱钩；收线回到抛出起点 → 钓上；脱钩后鱼消失、装备清空、线自动收回。
/// 挣扎实现只通过 ctx 的两个输出口表达意图：
///  ▪ ctx.PullVelocity —— 本帧把钩子拖走的运动速度（px/s，世界方向）；零 = 不拖（玩家按住左键 = 收线回拉）；
///  ▪ ctx.ReleaseRequested —— 主动脱钩请求（效果等同拉力拉断）。
/// 策略默认被同一至多份 FishDef 共享：无状态（每钩状态放 ctx 或复用 ctx.Struggle 数值机）才能安全复用文件实例。
/// </summary>
[GlobalClass]
public abstract partial class FishStruggleBase : Resource
{
    /// <summary>上钩瞬间（Hook.BeginHookUp 装配完成后）调用一次：做开场（复位自己的状态、注入初始值等）。默认空实现。</summary>
    public virtual void OnHooked(FishContext ctx) { }

    /// <summary>上钩挣扎每帧决策入口（Hook.StepHooked 每帧调用，之后按 ctx.PullVelocity / ctx.ReleaseRequested 执行）。</summary>
    public abstract void Update(FishContext ctx, double delta);
}
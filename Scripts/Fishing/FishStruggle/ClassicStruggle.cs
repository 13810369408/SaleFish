using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 内置挣扎策略（经典款）：体力/耐力/拉力数值机 + 每轮挣扎重选「远离抛出起点 ±60°」拖行方向，
/// 从原 Hook 内置挣扎逻辑逐行搬移，数值手感不变。
///
/// 数值输入照旧档位：体力/耐力/拖速三个最大值留在 FishDef 上，由 FishContext.Fish 给出；
/// 鱼线最大拉力取 ctx.MaxTension（Hook 的 Line. 配置）。普通鱼想要旧手感：Struggle 槽选
/// 「ClassicStruggle」即可，什么参数都不用配。
/// </summary>
[GlobalClass]
public partial class ClassicStruggle : FishStruggleBase
{
    public override void OnHooked(FishContext ctx)
    {
        // 数值注入（原本在 Hook.BeginHookUp 里）：体力/耐力 = 满值、拉力 0、默认在挣扎
        var fish = ctx.Fish;
        ctx.Struggle.BeginFight(
            fish?.Def?.MaxStamina ?? 10f,
            fish?.Def?.MaxEndurance ?? 10f,
            fish?.Def?.PullSpeed ?? 120f,
            ctx.MaxTension);
        ctx.StruggleWasStruggling = true;   // BeginFight 后默认在挣扎（首轮）

        // 挣扎反馈参考：首轮挣扎开始也播一次（震屏 + 特效，数据读 FishDef 「挣扎效果」分类）。
        ctx.PlayStruggleFeedback();
    }

    public override void Update(FishContext ctx, double delta)
    {
        var s = ctx.Struggle;
        s.Step(delta, ctx.ReelHeld);

        // 挣扎轮切换 → 重选拖行方向（每轮挣扎换一次 ±60° 方向的摆动）
        if (s.Struggling != ctx.StruggleWasStruggling)
        {
            ctx.StruggleWasStruggling = s.Struggling;
            if (s.Struggling)
            {
                ctx.PickDefaultPullDirection();

                // 挣扎反馈参考：每轮挣扎开始（Struggling 从 false → true）播一次震屏 + 特效。
                // 想换手感/加自己的反馈，在 FishDef 的「挣扎效果」分类里配好两个字段即可，这里不用再改。
                ctx.PlayStruggleFeedback();
            }
        }

        // 拉力拉断鱼线 → 脱钩
        if (s.Released) { ctx.ReleaseRequested = true; return; }

        // 输出拖行：挣扎 = 全速（按住左键施力减半）；安静 = 0（玩家可收线回拉）
        ctx.PullVelocity = s.Struggling ? ctx.PullDirection * s.CurrentPullSpeed(ctx.ReelHeld) : Vector2.Zero;

        // 输出动画播放状态：挣扎 → Struggle；体力耗尽 → Exhausted；其余（安静待收线/还没耗尽）→ Normal
        ctx.PlaybackState = s.Struggling ? FishPlayback.Struggle
            : s.Stamina <= 0f ? FishPlayback.Exhausted
            : FishPlayback.Normal;
    }
}
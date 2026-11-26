using Godot;
using SaleFish.Common;
using SaleFish.Fx;
using SaleFish.View;

namespace SaleFish.Fishing;

/// <summary>
/// 挣扎反馈的公共播放器（无钩子也可用，解耦自 FishContext）：给定鱼种定义 + 世界锚点位置，
/// 一行触发「震屏 + 特效」。
///
/// 数据全部来自 FishDef 「挣扎效果」分类的两个导出字段：
///  ▪ StruggleScreenShake —— 震屏预设（走全局 ScreenShaker.Primary?.Play()；没有则静默跳过）；
///  ▪ StruggleFxPrefab    —— 特效 prefab（从全局 ObjectPool 取实例，挂到场景树根、钉在世界位置，
///    之后鱼/钩子游走特效也不跟随；播完 Finished 自动还池，池不可用时退回「实例化 + 播完释放」）。
/// 两者任一为 null 即跳过该部分，可独立使用。
///
/// 调用方（游戏侧与演示关共用）：
///  ▪ 挣扎阶段：ctx.PlayStruggleFeedback()（FishContext 转调本类，锚点 = 钩子位置 + 钩子取树）；
///  ▪ 演示关/无钩子场合：StruggleFeedback.Play(def, anchorNode, worldPos)，锚点提供树源。
/// </summary>
public static class StruggleFeedback
{
    /// <summary>非 RingBurstEffect 自定义特效的兜底回收时长（秒）：没有 Finished 约定时按这个时间还池。</summary>
    private const float FxLifetimeFallbackSeconds = 1.5f;

    /// <summary>
    /// 播放一份挣扎反馈（一行调用）：震屏 + 特效。def 为 null 时直接返回；
    /// 两个字段均未配置也不报错。anchorNode 用来解析场景树根（作为特效挂载的父级，保证不跟鱼走）。
    /// </summary>
    public static void Play(FishDef? def, Node? anchorNode, Vector2 at)
    {
        if (def == null) return;
        if (def.StruggleScreenShake is { } shake) ScreenShaker.Primary?.Play(shake);
        SpawnFx(def.StruggleFxPrefab, anchorNode, at);
    }

    /// <summary>把特效 prefab 在给定世界位置播一次并自动回收（对象池优先）。
    /// 会先关掉 prefab 的 AutoPlay，再挂树，最后统一手动 Play——播放时机完全由这里决定。</summary>
    private static void SpawnFx(PackedScene? fxPrefab, Node? anchor, Vector2 at)
    {
        if (fxPrefab == null) return;

        // 挂到场景树根：特效固定在触发位置，父节点（场景根/调用方）不动 → 鱼游走特效不跟随
        if (anchor == null || !GodotObject.IsInstanceValid(anchor)) return;

        var pool = ObjectPool.Instance;
        var fx = pool != null
            ? pool.Get(fxPrefab, () => fxPrefab.Instantiate<Node2D>())
            : fxPrefab.Instantiate<Node2D>();

        if (fx is RingBurstEffect burst)
        {
            burst.AutoPlay = false;   // prefab 默认开着 → 新实例进树会提前自动播一次；统一关掉由这里手动 Play
            var tree = anchor.GetTree();
            var root = tree?.CurrentScene ?? anchor.GetParent() ?? anchor;
            root.AddChild(fx);
            fx.GlobalPosition = at;
            burst.Play();
            RingBurstEffect.FinishedEventHandler handler = null!;   // 播完还池（一次性订阅：退出时解绑，池复用不累积监听）
            handler = () => { burst.Finished -= handler; RecycleFx(fx); };
            burst.Finished += handler;
        }
        else
        {
            // 非 RingBurstEffect 的普通 prefab：无 Finished 信号约定 → 场景树计时器兜底回收（防泄漏）
            var tree = anchor.GetTree();
            var root = tree?.CurrentScene ?? anchor.GetParent() ?? anchor;
            root.AddChild(fx);
            fx.GlobalPosition = at;
            fx.Call("Play");
            var timer = tree?.CreateTimer(FxLifetimeFallbackSeconds);
            if (timer != null) timer.Timeout += () => RecycleFx(fx);
        }
    }

    /// <summary>杀青后把特效还回对象池（不可用则释放）。</summary>
    private static void RecycleFx(Node2D fx)
    {
        if (fx == null || !GodotObject.IsInstanceValid(fx)) return;
        var pool = ObjectPool.Instance;
        if (pool != null) pool.Return(fx);
        else fx.QueueFree();
    }
}
using Godot;

namespace SaleFish.Fx;

/// <summary>
/// 圆形扩张 + 散射粒子 组合特效（prefab 根节点，纯组件版）：把 ExpandingRing 与 RadialBurst
/// 两个都是 GPUParticles2D 引擎粒子的子节点合成一个可整体触发的 prefab。
/// 渲染与动画 100% 交给引擎组件：粒子发射/扩散/减速(damping)/放大(scale_curve)/淡出(color_ramp)
/// 全在子节点的 Process Material（ParticleProcessMaterial）配；本脚本只做编排——
/// Play() 按各自延迟触发（ExpandingRing 延迟 RingDelay 秒、RadialBurst 延迟 BurstDelay 秒，都从
/// Play() 起计时）、Stop() 总停、两个子特效都播完发一次 Finished（按各自延迟 + Lifetime 计时推算，
/// 不依赖 emitting 细节）。
/// GPU 粒子行为参数在子节点的 process_material 检查器改：圆形扩放的【放大倍率 scale_min/max、
/// 放大曲线 scale_curve / 淡出渐变 color_ramp】在 ExpandingRing 的 Process Material，粒子行为
/// （initial_velocity/damping/spread/color_ramp）在 RadialBurst 的 Process Material。
/// </summary>
public sealed partial class RingBurstEffect : Node2D
{
    /// <summary>圆形子节点路径（默认匹配 prefab 子节点名 ExpandingRing，类型 GPUParticles2D）。</summary>
    [Export] public NodePath ExpandingRingPath = new("ExpandingRing");

    /// <summary>粒子子节点路径（默认匹配 prefab 子节点名 RadialBurst，类型 GPUParticles2D）。</summary>
    [Export] public NodePath RadialBurstPath = new("RadialBurst");

    /// <summary>ExpandingRing（圆形）发动的延迟秒数（从 Play() 起计；0 = 与 Play() 同帧立即发动）。
    /// 默认 0：保持旧行为，圆形立刻扩张。</summary>
    [Export(PropertyHint.Range, "0,5,0.05")] public float RingDelay = 0f;

    /// <summary>RadialBurst（粒子）发动的延迟秒数（从 Play() 起计；0 = 与 Play() 同帧立即发动）。
    /// 默认 1：让扩散环先有一圈，再爆出粒子。</summary>
    [Export(PropertyHint.Range, "0,5,0.05")] public float BurstDelay = 0f;

    /// <summary>放入场景时自动播一次。</summary>
    [Export] public bool AutoPlay;

    /// <summary>两个子特效全部播完时触发。</summary>
    [Signal] public delegate void FinishedEventHandler();

    private GpuParticles2D? _ring;
    private GpuParticles2D? _burst;
    private bool _playing;
    private float _ringDelayRemaining;   // ExpandingRing 还差多少秒发动（Play 时 = RingDelay；0 = 已发动）
    private float _ringRemaining;        // 圆形还差多少秒播完（发动后按 Lifetime 初始化）
    private float _burstDelayRemaining;  // RadialBurst 还差多少秒启动（Play 时 = BurstDelay；0 = 已启动）
    private float _burstRemaining;       // 粒子还差多少秒播完（启动后按 Lifetime 初始化）

    /// <summary>是否正在播放（任一子特效还在播）。</summary>
    public bool IsPlaying => _playing;

    public override void _Ready()
    {
        _ring = GetNodeOrNull<GpuParticles2D>(ExpandingRingPath);
        _burst = GetNodeOrNull<GpuParticles2D>(RadialBurstPath);
        if (AutoPlay) Play();
    }

    public override void _Process(double delta) => Step(delta);

    /// <summary>推进一帧（公开步进，测试手动调用；ProcessMode 正常时引擎每帧自动调）。
    /// 递减两个子特效的延迟剩余，到点发动对应粒子；全部「延迟+播放」计时归零 → 触发 Finished。</summary>
    internal void Step(double delta)
    {
        if (!_playing) return;
        var dt = (float)delta;

        if (_ringDelayRemaining > 0f)
        {
            _ringDelayRemaining = Mathf.Max(_ringDelayRemaining - dt, 0f);
            if (_ringDelayRemaining <= 0f) StartRing();   // 圆形延迟到点 → 发动
        }
        if (_ringDelayRemaining <= 0f && _ringRemaining > 0f)
            _ringRemaining = Mathf.Max(_ringRemaining - dt, 0f);

        if (_burstDelayRemaining > 0f)
        {
            _burstDelayRemaining = Mathf.Max(_burstDelayRemaining - dt, 0f);
            if (_burstDelayRemaining <= 0f) StartBurst();   // 粒子延迟到点 → 发动
        }
        if (_burstDelayRemaining <= 0f && _burstRemaining > 0f)
            _burstRemaining = Mathf.Max(_burstRemaining - dt, 0f);

        if (_ringDelayRemaining <= 0f && _ringRemaining <= 0f
            && _burstDelayRemaining <= 0f && _burstRemaining <= 0f)
            OnChildFinished();
    }

    /// <summary>触发整个特效（圆形延迟 RingDelay 秒、粒子延迟 BurstDelay 秒后发动，都从 Play() 起计）。
    /// 任一延迟为 0 时对应粒子与 Play() 同帧启动。
    /// 开头先清场（Restart 清掉上次播放遗留的已发射粒子）：特效经 ObjectPool 池复用，节点重进树时
    /// 若不重置粒子时钟，旧环/旧粒子会原地残留，在延迟等待期闪现——先清再发，杜绝复播残影。</summary>
    public void Play()
    {
        _playing = true;
        _ringDelayRemaining = _ring != null ? Mathf.Max(RingDelay, 0f) : 0f;
        _ringRemaining = 0f;                       // 发动后才按 Lifetime 初始化（StartRing 里做）
        _burstDelayRemaining = _burst != null ? Mathf.Max(BurstDelay, 0f) : 0f;
        _burstRemaining = 0f;

        // 清场：清掉已发射粒子并复位发射状态（Restart 重置粒子时钟 → 已发射粒子消失；Emitting=false
        // 保证随后不重发）。0 延迟的粒子下面 StartXxx 会再 Emitting=true，不影响。
        if (_ring != null) { _ring.Emitting = false; _ring.Restart(); }
        if (_burst != null) { _burst.Emitting = false; _burst.Restart(); }

        if (_ring != null)
        {
            if (_ringDelayRemaining <= 0f) StartRing();   // 无延迟：同帧同步发（不等 _Process）
            else _ring.Emitting = false;                  // 有延迟：已清场，等 _Process 到点再发
        }
        if (_burst != null)
        {
            if (_burstDelayRemaining <= 0f) StartBurst();   // 无延迟：同帧同步发（不等 _Process）
            else _burst.Emitting = false;                   // 有延迟：已清场，等 _Process 到点再发
        }
    }

    /// <summary>立刻停止并清空（不发 Finished）：先 Restart 清掉已发射粒子、再停发射，避免残留
    /// （注意 GPUParticles2D.Restart() 会把 Emitting 重新打开，所以必须 Restart 之后才设 false）。</summary>
    public void Stop()
    {
        _playing = false;
        _ringDelayRemaining = 0f;
        _burstDelayRemaining = 0f;
        if (_ring != null) { _ring.Restart(); _ring.Emitting = false; }
        if (_burst != null) { _burst.Restart(); _burst.Emitting = false; }
    }

    /// <summary>发动 ExpandingRing（Play/_Process 里延迟到点时调用）：发粒子并把剩余播放时长初始化为 Lifetime。</summary>
    private void StartRing()
    {
        _ringRemaining = _ring != null ? (float)_ring.Lifetime : 0f;
        if (_ring != null)
        {
            _ring.Emitting = true;
            _ring.Restart();
        }
    }

    /// <summary>发动 RadialBurst（Play/_Process 里延迟到点时调用）：发粒子并把剩余播放时长初始化为 Lifetime。</summary>
    private void StartBurst()
    {
        _burstRemaining = _burst != null ? (float)_burst.Lifetime : 0f;
        if (_burst != null)
        {
            _burst.Emitting = true;
            _burst.Restart();
        }
    }

    private void OnChildFinished()
    {
        if (!_playing) return;   // 被 Stop 打断过，交给 Stop 处理
        _playing = false;
        EmitSignal(SignalName.Finished);
    }
}
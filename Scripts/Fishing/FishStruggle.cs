using System;

namespace SaleFish.Fishing;

/// <summary>
/// 上钩挣扎模拟（纯 C#，不依赖 Godot 节点，headless 可直接单测）。
/// 一条鱼上钩后的「体力 / 耐力 / 拉力」迷你博弈：
///
///  ▪ 体力（Stamina）：挣扎时每秒 −1（按帧折算 dt）。体内为 0 → 立即停止挣扎，之后的体力只减不增，永不再挣扎；
///  ▪ 耐力（Endurance）：挣扎时每秒 −1（按帧折算）。低于满值 50% 时每 0.5s 判一次「是否停止挣扎」，
///    停止概率随耐力降低线性增大（50%≈10% → 0%≈90%）；停止挣扎后每秒 +2 恢复（按帧折算、封顶满值）；
///  ▪ 重新挣扎：耐力高于满值 70% 且体力≠0 时，每 0.5s 判一次「是否重新挣扎」，
///    概率随耐力升高线性增大（70%≈0% → 100%≈80%）；
///  ▪ 拉力（Tension）：挣扎中按住左键施力 → 每秒上升；松开 → 每秒回落；达到 MaxTension → 脱钩（Released）。
///    施力同时把拖速减半（HoldPullFactor）——「按住左键：拖得慢但线绷紧」。
///
/// 本类只管数值与状态判定，不负责移动（钩子被拉走的方向/距离由 Hook 根据 Struggling 与 PullSpeed 执行）。
/// 概率判定要求可复现：构造后调 SeedRandom(seed) 锁随机序列（测试用）。
/// </summary>
public sealed class FishStruggle
{
    // ---- 规则常量（用户规格，暂不改） ----
    /// <summary>挣扎时体力每秒损失量（框架折算 dt）。</summary>
    public const float StaminaDrainPerSecond = 1f;
    /// <summary>挣扎时耐力每秒损失量（按帧折算 dt）。</summary>
    public const float EnduranceDrainPerSecond = 1f;
    /// <summary>停止挣扎后耐力每秒恢复量（按帧折算 dt）。</summary>
    public const float EnduranceRecoverPerSecond = 2f;
    /// <summary>耐力低于 50% 时「判停」/ 高于 70% 时「判重扎」的判定间隔（秒）。</summary>
    public const float CheckInterval = 0.5f;

    // ---- 施力参数（手感可调，默认用户拍板的数值） ----
    /// <summary>按住左键：鱼拖速 × 此因子（减缓拖动，代价是拉力上升）。</summary>
    public float HoldPullFactor = 0.5f;
    /// <summary>按住左键施力时拉力每秒上升量。</summary>
    public float TensionRisePerSecond = 40f;
    /// <summary>松开左键/不挣扎时拉力每秒回落量。</summary>
    public float TensionFallPerSecond = 80f;
    /// <summary>耐力=满值50%时的判停概率（线性低端）。</summary>
    public float StopChanceAtHalf = 0.1f;
    /// <summary>耐力=0 时的判停概率（线性高端）。</summary>
    public float StopChanceAtZero = 0.9f;
    /// <summary>耐力=满值100%时的重扎概率（线性高端，70% 处为 0）。</summary>
    public float RestruggleChanceAtFull = 0.8f;

    // ---- 上钩时注入的数值（来自 FishDef.MaxStamina/MaxEndurance/PullSpeed 与线 MaxTension） ----
    /// <summary>体力/耐力满值（上钩时两个都从满值开始）。</summary>
    public float MaxStamina;
    public float MaxEndurance;
    /// <summary>挣扎时把钩子带向远处的拖速（px/s，鱼种自定义）。</summary>
    public float PullSpeed;
    /// <summary>鱼线最大拉力（达到 → 脱钩）。</summary>
    public float MaxTension;

    // ---- 运行状态（Hook 每帧读取） ----
    /// <summary>当前体力。</summary>
    public float Stamina;
    /// <summary>当前耐力。</summary>
    public float Endurance;
    /// <summary>当前拉力。</summary>
    public float Tension;
    /// <summary>是否正在挣扎（挣扎=拖着鱼钩往远处跑；false=安静，可被收线拉回）。</summary>
    public bool Struggling;
    /// <summary>拉力达到鱼线最大承载力 → 脱钩（让鱼走）。</summary>
    public bool Released;

    private float _stopTimer;      // 低于 50% 判停的 0.5s 计时
    private float _restruggleTimer; // 高于 70% 判重扎的 0.5s 计时
    private Random _rng = new();

    /// <summary>用固定随机种子锁随机序列（测试可复现；默认不调用 = 乱序）。</summary>
    public void SeedRandom(int seed) => _rng = new Random(seed);

    /// <summary>上钩一口：注入鱼与线的数值；体力/耐力/拉力按规格复位（体力耐力=满值、拉力=0、默认在挣扎）。</summary>
    public void BeginFight(float maxStamina, float maxEndurance, float pullSpeed, float maxTension)
    {
        MaxStamina = maxStamina;
        MaxEndurance = maxEndurance;
        PullSpeed = pullSpeed;
        MaxTension = maxTension;
        Stamina = maxStamina;
        Endurance = maxEndurance;
        Tension = 0f;
        Released = false;
        Struggling = true;
        _stopTimer = 0f;
        _restruggleTimer = 0f;
    }

    /// <summary>推进一帧：holding = 玩家是否按住左键（挣扎中施力）。所有消耗/恢复都按 dt 折算，没有整秒跳变。</summary>
    public void Step(double delta, bool holding)
    {
        var dt = (float)delta;

        if (Struggling)
        {
            // 挣扎：体力/耐力同步每秒 −1（按帧折算）
            Stamina = MathF.Max(0f, Stamina - StaminaDrainPerSecond * dt);
            Endurance = MathF.Max(0f, Endurance - EnduranceDrainPerSecond * dt);

            if (Stamina <= 0f)
            {
                Struggling = false;   // 体力耗尽：立即停止，且不再触发挣扎（Stamina 不恢复）
                _restruggleTimer = 0f;
            }
            else if (Endurance < MaxEndurance * 0.5f)
            {
                _stopTimer += dt;                             // 每 0.5s 判一次是否停止
                while (_stopTimer >= CheckInterval)
                {
                    _stopTimer -= CheckInterval;
                    if (_rng.NextDouble() < StopChance(Endurance)) { Struggling = false; _restruggleTimer = 0f; break; }
                }
            }
            else _stopTimer = 0f;

            UpdateTension(dt, holding);
        }
        else
        {
            // 停止挣扎：耐力每秒 +2（按帧折算、封顶满值）
            Endurance = MathF.Min(MaxEndurance, Endurance + EnduranceRecoverPerSecond * dt);

            // 耐力 > 70% 且体力 ≠ 0 → 每 0.5s 判一次是否重新挣扎
            if (Stamina > 0f && Endurance > MaxEndurance * 0.7f)
            {
                _restruggleTimer += dt;
                while (_restruggleTimer >= CheckInterval)
                {
                    _restruggleTimer -= CheckInterval;
                    if (_rng.NextDouble() < RestruggleChance(Endurance)) { Struggling = true; _stopTimer = 0f; break; }
                }
            }
            else _restruggleTimer = 0f;

            UpdateTension(dt, holding: false);   // 安静时不施力：拉力回落
        }
    }

    /// <summary>当前挣扎拖速（按住左键施力时减半）。安静时返回 0（不拖钩）。</summary>
    public float CurrentPullSpeed(bool holding) => Struggling ? PullSpeed * (holding ? HoldPullFactor : 1f) : 0f;

    private void UpdateTension(float dt, bool holding)
    {
        if (Struggling && holding)
            Tension = MathF.Min(MaxTension, Tension + TensionRisePerSecond * dt);
        else
            Tension = MathF.Max(0f, Tension - TensionFallPerSecond * dt);

        if (Tension >= MaxTension && MaxTension > 0f)
        {
            Released = true;      // 绷到线能承受的上限 → 鱼脱钩
            Struggling = false;
        }
    }

    /// <summary>判停概率（纯函数）：耐力越低越大，在 [50%满值→StopChanceAtHalf, 0→StopChanceAtZero] 间线性。</summary>
    public float StopChance(float endurance)
    {
        var half = MaxEndurance * 0.5f;
        var k = half > 0f ? half : 0.0001f;   // 防除零（满值退化为 0 的配置）
        var t = MathF.Min(MathF.Max(1f - endurance / k, 0f), 1f);
        return StopChanceAtHalf + (StopChanceAtZero - StopChanceAtHalf) * t;
    }

    /// <summary>重扎概率（纯函数）：耐力越高越大，在 [70%满值→0, 100%满值→RestruggleChanceAtFull] 间线性。</summary>
    public float RestruggleChance(float endurance)
    {
        var span = MaxEndurance * 0.3f;   // 70%→100% 的跨度
        var k = span > 0f ? span : 0.0001f;   // 防除零（满值退化为 0 的配置）
        var t = MathF.Min(MathF.Max((endurance - MaxEndurance * 0.7f) / k, 0f), 1f);
        return RestruggleChanceAtFull * t;
    }
}
using System;
using System.Collections.Generic;
using Godot;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 鱼上钩系统测试。三部分：
///  ▪ A. 纯 C# 挣扎数值机（FishStruggle，无节点）：体力/耐力按帧每秒 −1、帧折算（10×0.1s == 1×1.0s）、
///    拉力「按住左键才涨、松开回落」、拉到 MaxTension → Released、体力归零 → 永久停止 + 耐力每秒 +2 恢复、
///    体力≠0 且耐力恢复满 → 有机会再挣扎（多固定种子属性测试）、判停/判重扎线性概率端点（纯函数）。
///  ▪ B. Hook 上钩集成（迷你关卡：Water + 真实 Hook + 鱼）：BeginHookUp 前置守卫（未咬中/没挂线/状态不对），
///    上钩后鱼成为钩子子节点、按鱼种注入数值；挣扎拖走（不按左键全速、按住减半 + 拉力上涨）、松开拉力回落；
///    脱钩两种出口（拉到线长 / 拉力拉断）→ 鱼消失 + 装备全清 + 鱼线自动收回 → 一竿结束；
///    钓上（挣扎停 → 收线拉回起点）→ 只清真饵/活饵、Caught 事件带鱼；挂线后抛出/下沉都被线长钳制；
///    收线水面间隙 X：上钩瞬间鱼定义（HookupSurfaceGap）数据驱动写入（B4/B5）、挣扎策略可经 FishContext
///    读改（B7），脱钩（B4）/ 钓上（B5/B7）后重设回钩子初始值。
///  ▪ C. 主关卡端到端（真实 main_stage.tscn + 真鱼群）：鱼咬中 → FishSpawner 广播 → MainStage 接上钩 →
///    挣扎停 → 收线 → Caught → 鱼物品直接生成到鼠标（HeldItemController）。
///
/// 钩子/鱼 ProcessMode 全部 Disabled + 手动 Step(dt) 驱动（同步执行，确定性），运行方式：
/// godot --headless --path &lt;project&gt; res://Scenes/TestHookup.tscn --quit-after 240
/// </summary>
public sealed partial class TestHookup : Node
{
    private const double Dt = 1.0 / 60.0;

    private int _passed;
    private int _failed;

    private Area2D _water = null!;

    public override void _Ready()
    {
        Run();
        GD.Print($"[HookupTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[HookupTest] ALL PASSED" : "[HookupTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }

    private void Run()
    {
        // ==================== A. 纯数值机 ====================
        TestStruggleSim();

        // ==================== B. Hook 上钩集成（迷你关卡） ====================
        // Water：Area2D + 矩形碰撞体（未旋转），水面/水底 = 全局包围盒
        _water = new Area2D { Name = "Water", GlobalPosition = new Vector2(500, 200) };
        var cs = new CollisionShape2D { Name = "CollisionShape2D" };
        cs.Shape = new RectangleShape2D { Size = new Vector2(2000, 400) };   // x −500..1500, y 0..400
        _water.AddChild(cs);
        AddChild(_water);

        TestHookupBasics();
        TestHookupGuards();
        TestReleaseByLineLength();
        TestReleaseByTension();
        TestCatchFlow();
        TestGapOverrideAndReset();
        TestRetractReelingRules();
        TestLineLengthCaps();

        // ==================== C. 主关卡端到端 ====================
        TestMainStageEndToEnd();

        GD.Print($"[HookupTest] 完成：PASS={_passed} FAIL={_failed}");
    }

    // ---- A. 纯数值机 ----

    private void TestStruggleSim()
    {
        // A1. BeginFight 复位：体力/耐力 = 满值、拉力 0、默认在挣扎、未脱钩
        var s = new FishStruggle();
        s.BeginFight(maxStamina: 100, maxEndurance: 100, pullSpeed: 100, maxTension: 100);
        Check("A1 上钩：体力/耐力=满值、拉力=0、在挣扎、未脱钩",
            s.Stamina == 100 && s.Endurance == 100 && s.Tension == 0f && s.Struggling && !s.Released);

        // A2. 每秒 −1 且按帧折算（10×0.1s 与 1×1.0s 效果相同）；耐力 >50% 时不判停（无随机干扰）
        for (var i = 0; i < 10; i++) s.Step(0.1, holding: false);
        Check("A2 挣扎 1s（10×0.1s）：体力/耐力各 −1", Mathf.Abs(s.Stamina - 99f) < 1e-3f && Mathf.Abs(s.Endurance - 99f) < 1e-3f);
        s.Step(1.0, holding: false);
        Check("A2 再挣扎 1s（1×1.0s 整帧）：同样 −1（帧折算成立）", Mathf.Abs(s.Stamina - 98f) < 1e-3f && Mathf.Abs(s.Endurance - 98f) < 1e-3f);

        // A3. 拉力：挣扎+按住才涨（40/s）、松开回落（80/s）
        s.BeginFight(100, 100, 100, 100);
        s.Step(0.25, holding: true);
        Check("A3 挣扎按住 0.25s：拉力 +10（40/s）", Mathf.Abs(s.Tension - 10f) < 0.2f, $"实际 {s.Tension:F2}");
        Check("A3 挣扎按住：体力耐力照常 -0.25", Mathf.Abs(s.Stamina - 99.75f) < 0.1f);
        s.Step(0.25, holding: false);
        Check("A3 松开 0.25s：拉力回落至 0（80/s）", s.Tension == 0f, $"实际 {s.Tension:F2}");

        // A4. 拉力顶到 MaxTension → Released（脱钩），之后不再挣扎、保持 Released
        s.BeginFight(100, 100, 100, maxTension: 40);
        for (var i = 0; i < 70; i++) s.Step(Dt, holding: true);   // 按住 1.16s → 拉力 40 顶到上限
        Check("A4 拉到最大拉力：Released（脱钩）且立即停止挣扎", s.Released && !s.Struggling);
        Check("A4 拉力被钳在 MaxTension 内", s.Tension <= 40f + 1e-3f);
        s.Step(1.0, holding: false);
        Check("A4 脱钩后保持 Released（不复活），拉力回落", s.Released && s.Tension == 0f);

        // A5. 施力减半：挣扎中 CurrentPullSpeed 按住 = 一半（用新机器：A4 那台已 Released = 安静）
        var s5 = new FishStruggle();
        s5.BeginFight(100, 100, 100, 100);
        Check("A5 施力时拖速减半（挣扎中 按住 = PullSpeed×0.5，松开 = 全速）",
            Mathf.Abs(s5.CurrentPullSpeed(true) - 50f) < 1e-4f && Mathf.Abs(s5.CurrentPullSpeed(false) - 100f) < 1e-4f);
        // 安静 = 不拖钩（玩家可正常收线）：用体力耗尽的机器确认 0——不用 A4 的 s，它 Released 后耐力回满
        // 有概率重新挣扎（耐力恢复机制），不能保证安静；MaxEndurance 给大数避免耐力 <50% 判停（纯线性耗尽才确定）
        var quiet = new FishStruggle();
        quiet.BeginFight(10, 100, 100, 1000);
        for (var i = 0; i < 10; i++) quiet.Step(1.0, holding: false);   // 挣扎 10s：体力耗尽 → 永久安静
        Check("A5 安静时拖速 = 0（不拖钩，玩家可正常收线）", quiet.CurrentPullSpeed(false) == 0f);

        // A6. 体力归零 → 立即停止、永久停止（耐力恢复满也不重扎）；停后耐力每秒 +2（封顶满值）
        var s2 = new FishStruggle();
        s2.BeginFight(maxStamina: 10, maxEndurance: 100, pullSpeed: 1, maxTension: 1000);
        for (var i = 0; i < 10; i++) s2.Step(1.0, holding: false);   // 挣扎 10s：体力 10→0
        Check("A6 体力耗尽（挣扎 10s）：立即停止挣扎", s2.Stamina == 0f && !s2.Struggling);
        for (var i = 0; i < 5; i++) s2.Step(1.0, holding: false);    // 停后 5s：耐力 +2/s → 满值
        Check("A6 停后耐力每秒 +2 恢复（5s 从 ≤10 → 满值 100）", Mathf.Abs(s2.Endurance - 100f) < 1e-3f, $"实际 {s2.Endurance:F1}");
        for (var i = 0; i < 10; i++) s2.Step(1.0, holding: false);
        Check("A6 体力为 0 时耐力满也不重扎（永久安静）", s2.Stamina == 0f && !s2.Struggling);

        // A7. 体力≠0 且耐力恢复 → 有机会再挣扎：多固定种子属性测试（种子固定 → 结果确定性）
        var reStruggled = 0;
        for (var seed = 0; seed < 5; seed++)
        {
            var sim = new FishStruggle();
            sim.SeedRandom(seed);
            sim.BeginFight(maxStamina: 1000, maxEndurance: 10, pullSpeed: 1, maxTension: 100000);
            var stopped = false;
            var restarted = false;
            for (var i = 0; i < 60 * 60; i++)   // 60s
            {
                sim.Step(Dt, holding: false);
                if (!stopped && !sim.Struggling) stopped = true;
                if (stopped && sim.Struggling) restarted = true;
            }
            if (stopped && restarted) reStruggled++;
        }
        Check("A7 5 个固定种子：挣扎停后都再挣扎过（耐力恢复 + 体力未耗尽）", reStruggled == 5, $"实际 {reStruggled}/5 重扎");

        // A8. 判停/判重扎线性概率端点（纯函数）
        var p = new FishStruggle();
        p.BeginFight(100, maxEndurance: 100, pullSpeed: 1, maxTension: 100);
        Check("A8 判停概率：耐力 50% ≈ 10%、0 ≈ 90%，单调递增",
            Mathf.Abs(p.StopChance(50) - 0.1f) < 1e-4f && Mathf.Abs(p.StopChance(0) - 0.9f) < 1e-4f
            && p.StopChance(25) > p.StopChance(50) && p.StopChance(25) < p.StopChance(0));
        Check("A8 判重扎概率：耐力 70% = 0、100% = 80%，单调递增",
            Mathf.Abs(p.RestruggleChance(70) - 0f) < 1e-6f && Mathf.Abs(p.RestruggleChance(100) - 0.8f) < 1e-4f
            && p.RestruggleChance(85) > 0f && p.RestruggleChance(85) < p.RestruggleChance(100));
    }

    // ---- 迷你关卡构造器 ----

    /// <summary>建一个带鱼线的真实 Hook（进树后 _Ready 记下 CastOrigin = 抛出起点），关掉真实物理循环。</summary>
    private Hook MakeHook(Vector2 home, float maxTension, float length)
    {
        var hook = new Hook
        {
            Position = home,
            Line = new FishingLineDef { Id = 99, DisplayName = "测试鱼线", MaxTension = maxTension, Length = length },
        };
        AddChild(hook);
        hook.ProcessMode = ProcessModeEnum.Disabled;
        hook.ConnectWater(_water);
        return hook;
    }

    /// <summary>建一条鱼（进树接线 Water/BaitSource；行为/挣扎策略从 Def 两个槽带出，无则空槽：行为空 = 不动只漂）。</summary>
    private Fish MakeFish(Vector2 pos, FishDef def, Hook bait)
    {
        var fish = new Fish { Def = def, BehaviorStrategy = def.BehaviorStrategy, Water = _water, BaitSource = bait, GlobalPosition = pos };
        AddChild(fish);
        fish.ProcessMode = ProcessModeEnum.Disabled;
        return fish;
    }

    private static void DriveToBite(Fish fish)
    {
        for (var i = 0; i < 8 && fish.Current != Fish.State.Bitten; i++) fish.Step(Dt);
    }

    /// <summary>把钩子抛进水里并下沉（Step 驱动直到 Sinking；测试把鱼放到下沉点附近再咬钩）。</summary>
    private static void CastIntoSinking(Hook hook)
    {
        hook.Cast(0f);
        StepUntil(hook, 600, () => hook.Current == Hook.State.Sinking);
    }

    private static void StepUntil(Hook hook, int maxSteps, Func<bool> stop)
    {
        for (var i = 0; i < maxSteps && !stop(); i++) hook.Step(Dt);
    }

    private static void SetEquipment(Hook hook)
    {
        hook.HookDef = new FishingHookDef { Id = 1, DisplayName = "测试钩" };
        hook.Bait = new BaitDef { Id = 1, DisplayName = "测试饵" };
        hook.LiveBait = new FishDef { Id = 22, DisplayName = "测试活饵" };
        hook.Sinker = new SinkerDef { Id = 1, DisplayName = "测试坠" };
    }

    // ---- B. 上钩集成 ----

    private void TestHookupBasics()
    {
        var def = new FishDef
        {
            Id = 9, DisplayName = "测试鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 100,
            BehaviorStrategy = new ClassicBehavior(),   // 行为用内置（保证能咬钩 + 上钩前后语义不变）
            Struggle = new ClassicStruggle(),           // 挣扎用内置数值机（B 段回归旧手感）
        };
        var hook = MakeHook(new Vector2(600, 300), maxTension: 80, length: 400);
        SetEquipment(hook);
        CastIntoSinking(hook);
        var sinkPos = hook.GlobalPosition;
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);   // 离起点 60px 处上钩（它的挣扎方向 = 远离起点）
        Check("B1 前置：钩子已在水里下沉（Sinking）", hook.Current == Hook.State.Sinking, $"实际 {hook.Current}");

        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);
        Check("B1 前置：鱼已咬中（Bitten）", fish.Current == Fish.State.Bitten);

        var ok = hook.BeginHookUp(fish);
        Check("B1 上钩成功（前置满足）", ok);
        Check("B1 钩子进入 Hooked（忙）", hook.Current == Hook.State.Hooked && hook.IsBusy);
        Check("B1 鱼成为钩子的子节点（一体）", fish.GetParent() == hook);
        Check("B1 鱼对齐钩子位置", (fish.GlobalPosition - hook.GlobalPosition).Length() < 0.01f);
        Check("B1 数值按鱼种注入：体力/耐力=30、拉力=0、在挣扎", hook.Struggle.Stamina == 30 && hook.Struggle.Endurance == 30
            && hook.Struggle.Tension == 0f && hook.Struggle.Struggling);
        Check("B1 上钩不清装备", hook.HookDef != null && hook.Bait != null && hook.LiveBait != null && hook.Sinker != null);

        // 挣扎·不按左键：全速拖走 100px/s × 0.5s = 50px；拉力 0（没施力）
        var p0 = hook.GlobalPosition;
        var out0 = hook.LineOut;
        for (var i = 0; i < 30; i++) hook.Step(Dt);
        var d0 = (hook.GlobalPosition - p0).Length();
        Check("B1 挣扎拖走 0.5s ≈ 50px（不按左键全速）", Mathf.Abs(d0 - 50f) < 1.2f, $"实际 {d0:F1}px");
        // 拖行主方向 = 远离抛出起点（摆动 ≤60° → 径向分量 ≥ 半速）：距起点必增不减
        Check("B1 拖行主方向远离抛出起点（距起点在拉长）", hook.LineOut - out0 > 20f, $"线长增 {(hook.LineOut - out0):F1}px");
        Check("B1 不施力拉力保持 0", hook.Struggle.Tension == 0f);

        // 挣扎·按住左键 = 施力：拖速减半（50px/s×0.25s≈12.5px）+ 拉力上涨（40/s×0.25s=10）
        hook.ReelHeld = true;
        p0 = hook.GlobalPosition;
        for (var i = 0; i < 15; i++) hook.Step(Dt);
        var d1 = (hook.GlobalPosition - p0).Length();
        Check("B1 按住左键施力：拖速减半（0.25s ≈ 12.5px）", Mathf.Abs(d1 - 12.5f) < 1.5f, $"实际 {d1:F1}px");
        Check("B1 施力时拉力上涨 ≈ 10", Mathf.Abs(hook.Struggle.Tension - 10f) < 0.3f, $"实际 {hook.Struggle.Tension:F2}");

        // 松开：拉力回落 80/s × 0.2s = 16 → 0
        hook.ReelHeld = false;
        for (var i = 0; i < 12; i++) hook.Step(Dt);
        Check("B1 松开左键：拉力回落至 0", hook.Struggle.Tension == 0f, $"实际 {hook.Struggle.Tension:F2}");
        hook.ReelHeld = false;
    }

    private void TestHookupGuards()
    {
        var def = new FishDef
        {
            Id = 10, DisplayName = "守卫鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 100,
            BehaviorStrategy = new ClassicBehavior(), Struggle = new ClassicStruggle(),
        };

        // 未咬中（还在巡逻）→ 拒绝
        var h1 = MakeHook(new Vector2(600, 300), maxTension: 80, length: 400);
        CastIntoSinking(h1);
        var f1 = MakeFish(h1.GlobalPosition + new Vector2(8, 4), def, h1);   // 不驱动到 Bitten
        Check("B2 未咬中的鱼不能上钩", !h1.BeginHookUp(f1));

        // 咬中了但钩子没挂鱼线 → 拒绝
        var h2 = MakeHook(new Vector2(600, 300), maxTension: 80, length: 400);
        h2.Line = null;
        CastIntoSinking(h2);
        var f2 = MakeFish(h2.GlobalPosition + new Vector2(8, 4), def, h2);
        DriveToBite(f2);
        Check("B2 咬中了但没挂鱼线 → 拒绝", !h2.BeginHookUp(f2));

        // 咬中了但钩子不在水里（Idle）→ 拒绝
        var h3 = MakeHook(new Vector2(600, 300), maxTension: 80, length: 400);   // 不 Cast：Idle
        var f3 = MakeFish(h3.CastOrigin + new Vector2(8, 4), def, h3);
        DriveToBite(f3);
        Check("B2 钩子没在水里（Idle）→ 拒绝", h3.Current == Hook.State.Idle && !h3.BeginHookUp(f3));
    }

    private void TestReleaseByLineLength()
    {
        // 线长 200：上钩后挣扎把钩子拉到线长极限 → 直接脱钩，鱼消失、装备全清、鱼线自动收回 → 一竿结束
        var def = new FishDef
        {
            Id = 11, DisplayName = "拉线鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 100,
            BehaviorStrategy = new ClassicBehavior(), Struggle = new ClassicStruggle(),
        };
        var hook = MakeHook(new Vector2(600, 300), maxTension: 1000, length: 200);
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);   // 线长外还有 140px 可拉
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);
        Check("B3 前置：上钩成功", hook.BeginHookUp(fish));

        var p0 = hook.GlobalPosition;
        var released = false;
        for (var i = 0; i < 400 && !released; i++)
        {
            hook.Step(Dt);
            // 脱钩信号：装备被清空（ReleaseFish 唯一清装备的路径）
            if (hook.HookDef == null) released = true;
        }
        Check("B3 挣扎拉到线长 → 脱钩（装备被清空）", released);
        Check("B3 脱钩触发点是线长极限（不是拉力）", hook.Struggle is { Released: false });
        Check("B3 脱钩时已超出原位置（鱼确实拉了一段）", (hook.GlobalPosition - p0).Length() > 20f);
        Check("B3 鱼直接消失（不再是钩子子节点）", fish.GetParent() != hook);
        Check("B3 脱钩清空全部装备（钩/饵/活饵/坠）", hook.HookDef == null && hook.Bait == null && hook.LiveBait == null && hook.Sinker == null);

        var finalPos = hook.GlobalPosition;
        StepUntil(hook, 600, () => hook.Current == Hook.State.Rest);
        Check("B3 鱼线自动缓慢收回起点 → 一竿结束（Rest）", hook.Current == Hook.State.Rest, $"实际 {hook.Current}");
        Check("B3 收回后回到抛出起点附近", (hook.GlobalPosition - hook.CastOrigin).Length() < 120f, $"距起点 {(hook.GlobalPosition - hook.CastOrigin).Length():F0}px（从 {finalPos} 收）");
        Check("B3 一竿结束：钩子不忙", !hook.IsBusy);
    }

    private void TestReleaseByTension()
    {
        // 最大拉力 30：挣扎中按住左键施力 → 拉力 40/s 顶到 30（~0.75s）→ 拉断脱钩（同上清理路径）
        var def = new FishDef
        {
            Id = 12, DisplayName = "绷线鱼", MaxStamina = 30, MaxEndurance = 30, PullSpeed = 100,
            BehaviorStrategy = new ClassicBehavior(), Struggle = new ClassicStruggle(),
            HookupSurfaceGap = 100f,   // 上钩瞬间把收线水面间隙 X 改成 100（脱钩时应重设回 10）
        };
        var hook = MakeHook(new Vector2(600, 300), maxTension: 30, length: 2000);
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);
        Check("B4 前置：上钩成功", hook.BeginHookUp(fish));
        Check("B4 上钩瞬间：鱼定义 HookupSurfaceGap=100 把收线水面间隙改成 100",
            Mathf.Abs(hook.CurrentReelSurfaceGap - 100f) < 1e-4f, $"实际 {hook.CurrentReelSurfaceGap}");

        hook.ReelHeld = true;   // 全程按住施力
        StepUntil(hook, 300, () => hook.Struggle.Released);
        Check("B4 施力把拉力顶到最大 → Released（拉断脱钩）", hook.Struggle.Released);
        Check("B4 拉断后：鱼消失、装备全清", fish.GetParent() != hook
            && hook.HookDef == null && hook.Bait == null && hook.LiveBait == null && hook.Sinker == null);
        Check("B4 脱钩：拉力归零（拉断瞬间的满拉力被清掉）", hook.Struggle.Tension == 0f, $"实际 {hook.Struggle.Tension:F2}");
        Check("B4 脱钩：收线水面间隙重设回钩子初始值（10）",
            Mathf.Abs(hook.CurrentReelSurfaceGap - 10f) < 1e-4f, $"实际 {hook.CurrentReelSurfaceGap}");

        StepUntil(hook, 1200, () => hook.Current == Hook.State.Rest);
        Check("B4 脱钩后鱼线自动收回 → 一竿结束（Rest）", hook.Current == Hook.State.Rest, $"实际 {hook.Current}");
        hook.ReelHeld = false;
    }

    private void TestCatchFlow()
    {
        // 体力 5：无论怎么挣扎，5 秒后体力耗尽 → 永久安静 → 按住左键收线拉回起点 → 钓上
        var def = new FishDef
        {
            Id = 13, DisplayName = "上钩鱼", MaxStamina = 5, MaxEndurance = 5, PullSpeed = 120,
            BehaviorStrategy = new ClassicBehavior(), Struggle = new ClassicStruggle(),
            HookupSurfaceGap = 100f,   // 上钩瞬间把收线水面间隙 X 改成 100（钓上时应重设回 10）
        };
        var hook = MakeHook(new Vector2(600, 300), maxTension: 60, length: 2000);
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);

        Fish? caught = null;
        var caughtCount = 0;
        hook.Caught += f => { caught = f; caughtCount++; };
        Check("B5 前置：上钩成功", hook.BeginHookUp(fish));
        Check("B5 上钩瞬间：鱼定义 HookupSurfaceGap=100 把收线水面间隙改成 100",
            Mathf.Abs(hook.CurrentReelSurfaceGap - 100f) < 1e-4f, $"实际 {hook.CurrentReelSurfaceGap}");

        // 不按左键等它挣扎完（体力耗尽快则 ≤5s，远小于线长 2000）
        StepUntil(hook, 1500, () => !hook.Struggle.Struggling && hook.Struggle.Stamina <= 0f);
        Check("B5 挣扎停止（体力耗尽，永久安静）", !hook.Struggle.Struggling && hook.Struggle.Stamina == 0f);
        Check("B5 安静时未按住左键：钩子原地悬停", hook.Current == Hook.State.Hooked);

        // 按住左键 = 正常收线拉回
        hook.ReelHeld = true;
        StepUntil(hook, 3000, () => hook.Current == Hook.State.Rest);
        Check("B5 收线拉回起点 → 钓上（一竿结束 Rest）", hook.Current == Hook.State.Rest, $"实际 {hook.Current}");
        Check("B5 Caught 事件触发一次且携带这条鱼", caughtCount == 1 && ReferenceEquals(caught, fish));
        Check("B5 钓上只清真饵/活饵（钩子、铅坠保留）", hook.Bait == null && hook.LiveBait == null
            && hook.HookDef != null && hook.Sinker != null, $"Bait={hook.Bait} LiveBait={hook.LiveBait} Sinker={hook.Sinker}");
        Check("B5 鱼不再挂在钩子上", fish.GetParent() != hook);
        Check("B5 钓上：收线水面间隙重设回钩子初始值（10）",
            Mathf.Abs(hook.CurrentReelSurfaceGap - 10f) < 1e-4f, $"实际 {hook.CurrentReelSurfaceGap}");
        Check("B5 钓上：拉力同样归零", hook.Struggle.Tension == 0f, $"实际 {hook.Struggle.Tension:F2}");
        hook.ReelHeld = false;
    }

    private void TestGapOverrideAndReset()
    {
        // R5/R6：上钩的鱼在 OnHooked 里经 FishContext 读取并覆写「收线水面间隙 X」；
        // gap 覆写前应是 FishDef.HookupSurfaceGap 数据驱动的 100（证明读到的是「当前生效值」），
        // 覆写成 25 后钓上 → 重设回钩子初始值 10。GapTinkerStruggle 全程安静（不掺挣扎数值）。
        var def = new FishDef
        {
            Id = 14, DisplayName = "调X鱼",
            MaxStamina = 5, MaxEndurance = 5, PullSpeed = 120,
            BehaviorStrategy = new ClassicBehavior(),
            Struggle = new GapTinkerStruggle(),
            HookupSurfaceGap = 100f,
        };
        var tinker = (GapTinkerStruggle)def.Struggle!;
        var hook = MakeHook(new Vector2(600, 300), maxTension: 1000, length: 2000);
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = hook.CastOrigin + new Vector2(60, 20);
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);
        Check("B7 前置：上钩成功", hook.BeginHookUp(fish));

        Check("B7 上钩：鱼在 OnHooked 里读到 def 数据驱动写入的 X=100",
            Mathf.Abs(tinker.ReadOnHooked - 100f) < 1e-4f, $"读到 {tinker.ReadOnHooked}");
        Check("B7 上钩：鱼经 FishContext 把 X 覆写成 25（立即生效）",
            Mathf.Abs(hook.CurrentReelSurfaceGap - 25f) < 1e-4f, $"实际 {hook.CurrentReelSurfaceGap}");

        // 安静鱼：按住左键 = 收线拉回起点 → 钓上
        hook.ReelHeld = true;
        StepUntil(hook, 3000, () => hook.Current == Hook.State.Rest);
        Check("B7 安静鱼收线拉回起点 → 钓上（Rest）", hook.Current == Hook.State.Rest, $"实际 {hook.Current}");
        Check("B7 钓上：X 重设回钩子初始值（10）",
            Mathf.Abs(hook.CurrentReelSurfaceGap - 10f) < 1e-4f, $"实际 {hook.CurrentReelSurfaceGap}");
        hook.ReelHeld = false;
    }

    private void TestRetractReelingRules()
    {
        // 需求 1/3：脱钩后的自动收线也要走三档收线规则——浅水处脱钩，不再「无视重力直线收回」，
        // 而是先受重力沿圆弧下摆（线只缩短 + 重力 + 勾股）→ 到弧底后被线拖向锚点 → 线收完一竿结束。
        // 钩子：home=(600,-100)（水面上方）、X=60 → 容差带 [54,66]、拉升区 x∈[550,650]；浅水点 (900,10)。
        var def = new FishDef
        {
            Id = 15, DisplayName = "收线规则鱼", MaxStamina = 5, MaxEndurance = 5, PullSpeed = 100,
            BehaviorStrategy = new ClassicBehavior(), Struggle = new ClassicStruggle(),
        };
        var hook = MakeHook(new Vector2(600, -100), maxTension: 1000, length: 250);
        hook.ReelSurfaceGap = 60f;   // 测试专属 X
        SetEquipment(hook);
        CastIntoSinking(hook);
        hook.GlobalPosition = new Vector2(900f, 10f);   // 浅水（深度 10 < 带下沿 54）+ 拉升区外；LineOut≈320 ≥ 250 → 立即线长脱钩
        var fish = MakeFish(hook.GlobalPosition + new Vector2(8, 4), def, hook);
        DriveToBite(fish);
        Check("B8 前置：浅水处上钩成功", hook.BeginHookUp(fish));
        StepUntil(hook, 40, () => hook.HookDef == null);   // 线长脱钩（ReleaseFish 清装备）
        Check("B8 浅水线长脱钩", hook.HookDef == null, $"实际 HookDef={hook.HookDef}");

        // 自动收线前 30 帧（0.5s，水平 ~100px）：净位移应「水平移向锚点 + 因太浅而下沉（y 净增）」；
        // 旧实现是直线朝 (600,-100) 收回 → y 净减（穿过水面上升）。
        var start = hook.GlobalPosition;
        for (var i = 0; i < 30 && hook.Current == Hook.State.Hooked; i++) hook.Step(Dt);
        var descended = hook.GlobalPosition.Y - start.Y;
        var xAdvance = start.X - hook.GlobalPosition.X;
        Check("B8 自动收线太浅：先受重力下沉（y 净增），不是无视重力直线收",
            hook.Current == Hook.State.Hooked && descended > 2f && xAdvance > 60f,
            $"dy={descended:F1}px dx={xAdvance:F1}px");

        // 继续按圆弧收完：线长只缩不增（圆弧模型）——钩子沿不断缩短的线长圆弧继续下摆（y 再增大），
        // 到弧底（钩子悬在锚点正下方）后用线收短把钩子拖向锚点，线短到 ReelFinishDistance → 一竿结束。
        // 注意：钩子在 (900,10)（线长 ~319、收线 200px/s），重力下沉 ~30px/s —— 线收得比重力落得快，
        // 钩子在到达容差带 [54,66] 之前就会被重新拖回锚点上方，**不上浮到带**是圆弧模型的正常结果。
        var maxY = float.MinValue;
        var prevX = hook.GlobalPosition.X;
        var wentAway = false;
        for (var i = 0; i < 6000 && hook.Current != Hook.State.Rest; i++)
        {
            hook.Step(Dt);
            if (hook.Current != Hook.State.Hooked) break;
            maxY = Mathf.Max(maxY, hook.GlobalPosition.Y);
            if (hook.GlobalPosition.X > prevX + 0.5f) wentAway = true;   // 弧底后直线被拖回锚点，x 不再增加
            prevX = hook.GlobalPosition.X;
        }
        Check("B8 自动收线圆弧：继续下摆到弧底（y 显著 > 起点 10，旧直线收线会瞬间穿过水面上升回 -100）",
            maxY >= 20f, $"maxY={maxY:F2}");
        Check("B8 自动收线圆弧：水平只逼近锚点、从不折返（x 单调 → 600）", !wentAway, $"prevX={prevX:F1}");
        Check("B8 自动收线按圆弧规则收完 → 一竿结束（Rest）", hook.Current == Hook.State.Rest, $"实际 {hook.Current}");
    }

    private void TestLineLengthCaps()
    {
        // 线长 150：满力抛出/下沉都钳在起点半径 150 内（最远抛出距离 = 最深下沉距离 = 线长）
        var hook = MakeHook(new Vector2(600, 250), maxTension: 1000, length: 150);   // 半径 150 的圆弧全落在水区（y0..400）内
        var maxOut = 0f;
        hook.Cast(1.0f);
        for (var i = 0; i < 600 && hook.Current is Hook.State.Flying or Hook.State.Sinking; i++)
        {
            hook.Step(Dt);
            maxOut = Mathf.Max(maxOut, hook.LineOut);
        }
        Check("B6 抛出/下沉全程不越线长（≤150px）", maxOut <= 150.5f, $"实际最大 {maxOut:F1}px");
        Check("B6 确实绷到过线长极限（满力抛到顶住）", maxOut >= 149f, $"实际最大 {maxOut:F1}px");
        Check("B6 终点状态合法（飞行/下沉/收线/结束）",
            hook.Current is Hook.State.Flying or Hook.State.Sinking or Hook.State.Reeling or Hook.State.Rest,
            $"实际 {hook.Current}");
    }

    // ---- C. 主关卡端到端 ----

    private void TestMainStageEndToEnd()
    {
        var stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn").Instantiate<Main.MainStage>();
        AddChild(stage);
        stage.ProcessMode = ProcessModeEnum.Disabled;
        var hook = stage.GetNodeOrNull<Hook>("Hook")!;
        if (hook != null) hook.ProcessMode = ProcessModeEnum.Disabled;
        var spawner = stage.GetNodeOrNull<FishSpawner>("FishSpawner")!;
        if (spawner != null) spawner.ProcessMode = ProcessModeEnum.Disabled;

        Check("C1 场景有 Hook/FishSpawner", hook != null && spawner != null);
        if (hook == null || spawner == null) return;

        var fishCount = 0;
        var target = null as Fish;
        foreach (var child in spawner.GetChildren())
        {
            if (child is not Fish f) continue;
            fishCount++;
            target ??= f;
        }
        Check("C1 鱼群已生成（≥1 条，带 Def）", fishCount >= 1 && target != null && target.Def != null);
        if (target == null) return;

        // 换一根长线（避免挣扎拉脱；不动共享的 main_stage 资源）
        hook.Line = new FishingLineDef { Id = 88, DisplayName = "端到端长线", MaxTension = 100, Length = 20000 };
        hook.SeedRandom(123);   // 固定挣扎摆动方向，保证可复现

        // 抛竿 → 入水下沉
        hook.Cast(0.35f);
        StepUntil(hook, 900, () => hook.Current != Hook.State.Flying);
        Check("C2 抛竿入水下沉", hook.Current == Hook.State.Sinking, $"实际 {hook.Current}");
        if (hook.Current != Hook.State.Sinking) return;

        // 把鱼嘴贴到下沉点（钩子）上 → 鱼自己咬中（Bitten 事件 → FishSpawner 广播 → MainStage 接上钩）。
        // 咬钩锚点是鱼嘴（在鱼前方，偏移 = 精灵里 MouthOffset 换算的世界偏移）：按 MouthWorldOffset 反推鱼中心，
        // 让嘴落在钩子上。初始朝向朝右（FacingLeft=false，鱼还没动过）→ 偏移即朝右值，直接用它对齐。
        target.ProcessMode = ProcessModeEnum.Disabled;
        var bitePos = hook.GlobalPosition;
        target.GlobalPosition = bitePos - target.MouthWorldOffset;
        DriveToBite(target);
        Check("C2 鱼咬中（Bitten）", target.Current == Fish.State.Bitten);
        Check("C2 主关卡接上钩：Hooked（FishSpawner.FishBitten → MainStage → BeginHookUp）",
            hook.Current == Hook.State.Hooked, $"实际 {hook.Current}");
        Check("C2 上钩数值来自鱼种（体力=8 = Sartine 满值）", hook.Struggle.MaxStamina == 8f);
        var stillInSpawner = false;
        foreach (var child in spawner.GetChildren()) if (child == target) stillInSpawner = true;
        Check("C2 咬中的鱼离开鱼群、挂到钩子上", !stillInSpawner);

        // 等它挣扎完（体力 10 → 结束为止，不再按左键）
        StepUntil(hook, 2000, () => !hook.Struggle.Struggling && hook.Struggle.Stamina <= 0f);
        Check("C3 挣扎停止（体力耗尽）", hook.Struggle.Stamina == 0f && !hook.Struggle.Struggling);

        // 按住左键收线 → 钓上 → Caught → 物品生成到鼠标
        hook.ReelHeld = true;
        StepUntil(hook, 4000, () => hook.Current == Hook.State.Rest);
        Check("C3 收线拉回 → 一竿结束（Rest）", hook.Current == Hook.State.Rest, $"实际 {hook.Current}");
        hook.ReelHeld = false;

        var hand = HeldItemController.Instance;
        var held = hand?.HeldItem;
        var itemDef = target.Def?.ItemDef;
        Check("C3 钓上的物品挂到了鼠标上（HeldItemController.TakeExternal）", held != null, $"HeldItem={held}");
        Check("C3 物品来自鱼的 ItemDef（占格 2）",
            held is { Data: PlacementItemTypeDef pid } && itemDef != null && ReferenceEquals(pid, itemDef) && held.Shape.CellCount == 2,
            held == null ? "空手" : $"格数={held.Shape.CellCount}");
        Check("C3 钓上后只清真饵/活饵（鱼钩保留）", hook.Bait == null && hook.LiveBait == null && hook.HookDef != null,
            $"Bait={hook.Bait} LiveBait={hook.LiveBait} HookDef={hook.HookDef}");
        Check("C3 被钓上的鱼已从钩子消失", target.GetParent() != hook);
        if (hand != null) hand.DiscardHeld();   // 清理手持，避免影响后续
    }

    /// <summary>测试挣扎策略：OnHooked 里先读「收线水面间隙 X」（应 = FishDef.HookupSurfaceGap 数据驱动写入后的值），
    /// 再经 FishContext.ReelSurfaceGap 覆写成 25；Update 安静不拖（不掺入挣扎数值，专测 X 的鱼侧读写 + 重设）。</summary>
    private sealed partial class GapTinkerStruggle : FishStruggleBase
    {
        public float ReadOnHooked = float.NaN;

        public override void OnHooked(FishContext ctx)
        {
            ReadOnHooked = ctx.ReelSurfaceGap;   // 读：应该是 def 数据驱动写入的 100
            ctx.ReelSurfaceGap = 25f;            // 写：覆写成 25
        }

        public override void Update(FishContext ctx, double delta) { }   // 安静：不拖钩（留给收线钓上）
    }
}
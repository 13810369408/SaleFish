using System;
using Godot;
using SaleFish.Fishing;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 钓鱼交互集成测试：实例化正式主关卡 scenes/main_stage.tscn，验证：
///  ▪ 蓄力条三角波纯函数 ChargeRatio（0→100→0，2 秒循环）；
///  ▪ 抛竿改为鼠标左键：空手 + 钩子空闲时点击左键 = 抛竿（蓄力条出现在玩家顶端）；再点左键 = 抛出
///    （蓄力条隐藏、钩子进入飞行，初速 = (MinSpeed, 上抛vy0)）；已无「钓竿」按钮；
///  ▪ 空中重力：vy 先加 AirGravity×GravityBase×δt，再减空气阻力 AirResist×vy×δt/√Weight
///    （g 恒定、重量只减阻力项——更重落得更快但不改变 g）；水平速度不变；飞行中 BeginReel 无效（仅落水后生效）；
///  ▪ 水中下沉：入水后套用「900 重力 + 水中阻力 WaterResist」模型（与空气同构、g 不变；重量只缩放阻力项）——
///    迅速刹到水中终端速度 g×√Weight/WaterResist 后恒定匀速下潜（WaterResist 默认 8 → W=1 ≈112.5）；
///    水平同用水中阻力快速衰减停稳；
///  ▪ 镜头跟随：抛竿后 1 秒内 easeOut 把鱼钩平滑带到画面中央，之后持续钉住；
///    一竿结束（Rest → Finished）后 1 秒内从跟随位置平缓回到抛竿前记录的初始位置；
///  ▪ 收线（Reeling）：落水后 BeginReel → 线长锁定为点击瞬间到起点距离；不按左键时受重力+线长约束
///    沿圆弧荡向起点正下方（线长保持恒定、向左下方画弧），荡到后停住（钩子正下方、线竖直），停住一竿不算结束；
///    按住左键（ReelHeld）→ 按「收线水面间隙 X + 最终拉升区」收线：深处仍然直线朝起点（旧行为），
///    深度骑到 X±10% 保持高度、水平移向起点，太浅或水上收线同时受重力下降，进入起点正下方 ±LiftUnlockRange
///    后忽略 X 直线收回；收完（距起点<ReelFinishDistance）→ Rest、可再次抛竿
///    （三档/拉升区的精确行为在 TestFishWater.TestReelSurfaceTiers 单测，这里只断言集成不变量）；
///    松开左键立刻恢复重力：沿收紧后的新线长圆弧摆回（弧线碰到水底就重新落回水底）；
///  ▪ 不点左键：垂沉到水底 → 停在水底（不结束一竿），随时可按左键继续收线收完。
///
/// 钩子运动自包含（Hook.Step / MainStage.StepCameraFollow 公开步进），测试把两处
/// ProcessMode 关掉后手动步进，完全确定性；Water 判定走矩形碰撞体几何包围盒，不依赖物理帧。
/// 鼠标输入合成 InputEventMouseButton 直接派给 MainStage._UnhandledInput（与 TestMainStage 同路）。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestFishing.tscn --quit-after 240
/// </summary>
public sealed partial class TestFishing : Node
{
    private int _passed;
    private int _failed;

    private Main.MainStage _stage = null!;
    private Hook _hook = null!;
    private Camera2D _camera = null!;
    private ProgressBar _powerBar = null!;

    /// <summary>当前场景 Water 矩形碰撞体全局包围盒（与 Hook.WaterRect 同逻辑、运行时动态读取——水挪了/改大了测试依然成立）；旋转/异形时回退 null。</summary>
    private static Rect2? WaterRect(Main.MainStage stage)
    {
        var water = stage.GetNodeOrNull<Area2D>("Water");
        if (water?.GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return null;
        if (cs.Shape is not RectangleShape2D rect || cs.Rotation != 0f) return null;
        var size = rect.Size * cs.GlobalScale;
        return new Rect2(cs.GlobalPosition - size / 2f, size);
    }

    public override void _Ready()
    {
        // 开局阶段随存档走：本测试的既有断言都以「准备钓鱼」(Ready) 为前提（等价读一个坐在椅子上的档）。
        SaleFish.Save.SaveManager.InFreeMove = false;
        _stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn")
            .Instantiate<Main.MainStage>();
        AddChild(_stage);

        _hook = _stage.GetNodeOrNull<Hook>("Hook")!;
        _camera = _stage.GetNodeOrNull<Camera2D>("Camera2D")!;
        _powerBar = Find("PowerBar") as ProgressBar ?? null!;

        // 停掉真实物理/处理循环的自动推进（避免与测试手动步进互相干扰），全部由 Step 驱动
        if (_hook != null) _hook.ProcessMode = ProcessModeEnum.Disabled;
        if (_stage != null) _stage.ProcessMode = ProcessModeEnum.Disabled;

        // 主关卡 Hook 现在挂了一条鱼线（basic_line）：鱼线会把「抛出最远距离/下沉最深深度」限制在线长内、
        // 允许鱼咬钩上钩——本测试专注抛/沉/收线的既有断言，先摘掉鱼线隔离新系统（上钩/线长约束在 TestHookup 单独测）
        if (_hook != null) _hook.Line = null;

        Run();
        GD.Print($"[FishingTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishingTest] ALL PASSED" : "[FishingTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>按名字找子节点（运行时代码创建的节点不被场景"拥有"，FindChild 需 owned=false）。</summary>
    private Node? Find(string name)
    {
        var n = _stage.FindChild(name, recursive: true, owned: false);
        if (n == null) GD.PrintErr($"[FishingTest] 找不到节点 {name}");
        return n;
    }

    private void Run()
    {
        Check("场景中有 Hook 节点", _hook != null);
        Check("场景中有 Water 区域", _stage.GetNodeOrNull<Area2D>("Water") != null);
        Check("场景中有 Camera2D", _camera != null);
        Check("相机锚点 = Drag Center（相机位置即屏幕中心，否则钩子会被钉到屏幕角落）",
            _camera != null && _camera.AnchorMode == Camera2D.AnchorModeEnum.DragCenter);
        Check("钩子初始空闲", _hook != null && _hook.Current == Hook.State.Idle);
        if (_hook == null || _powerBar == null || _camera == null) return;

        const double dt = 1.0 / 60.0;

        // ---- 1. 蓄力三角波纯函数 ----
        Check("ChargeRatio(0.0)=0", Mathf.Abs(Main.MainStage.ChargeRatio(0.0) - 0f) < 1e-6f);
        Check("ChargeRatio(0.5)=0.5", Mathf.Abs(Main.MainStage.ChargeRatio(0.5) - 0.5f) < 1e-6f);
        Check("ChargeRatio(1.0)=1", Mathf.Abs(Main.MainStage.ChargeRatio(1.0) - 1f) < 1e-6f);
        Check("ChargeRatio(1.5)=0.5", Mathf.Abs(Main.MainStage.ChargeRatio(1.5) - 0.5f) < 1e-6f);
        Check("ChargeRatio(2.0)=0（回到起点循环）", Mathf.Abs(Main.MainStage.ChargeRatio(2.0) - 0f) < 1e-6f);
        Check("ChargeRatio(2.5)=0.5（循环继续）", Mathf.Abs(Main.MainStage.ChargeRatio(2.5) - 0.5f) < 1e-6f);
        Check("ChargeRatio(3.0)=1（循环继续）", Mathf.Abs(Main.MainStage.ChargeRatio(3.0) - 1f) < 1e-6f);

        // 抛投蓄力窗口 ChargeToPower（蓄力值 0..100 → 力度 0..1；默认 10/90 两端有停顿死区）
        Check("ChargeToPower(5,10,90)=0（最近停顿区）", Mathf.Abs(Main.MainStage.ChargeToPower(5f, 10f, 90f)) < 1e-6f);
        Check("ChargeToPower(10,10,90)=0（恰好最小值=最近）", Mathf.Abs(Main.MainStage.ChargeToPower(10f, 10f, 90f)) < 1e-6f);
        Check("ChargeToPower(50,10,90)=0.5（窗口内线性）", Mathf.Abs(Main.MainStage.ChargeToPower(50f, 10f, 90f) - 0.5f) < 1e-6f);
        Check("ChargeToPower(90,10,90)=1（恰好最大值=最远）", Mathf.Abs(Main.MainStage.ChargeToPower(90f, 10f, 90f) - 1f) < 1e-6f);
        Check("ChargeToPower(95,10,90)=1（最远停顿区）", Mathf.Abs(Main.MainStage.ChargeToPower(95f, 10f, 90f) - 1f) < 1e-6f);
        Check("ChargeToPower：min>max 自动对调（填 70/80/60 → 60..80 中点=0.5）",
            Mathf.Abs(Main.MainStage.ChargeToPower(70f, 80f, 60f) - 0.5f) < 1e-6f);
        Check("ChargeToPower：0/100 = 旧直连（50 → 0.5）", Mathf.Abs(Main.MainStage.ChargeToPower(50f, 0f, 100f) - 0.5f) < 1e-6f);
        Check("ChargeToPower：min==max 退化直连（不除零）", Mathf.Abs(Main.MainStage.ChargeToPower(30f, 40f, 40f) - 0.3f) < 1e-6f);
        Check("默认窗口 = 10/90（最近/最远各 10% 停顿）",
            Mathf.Abs(_stage.MinChargeValue - 10f) < 1e-6f && Mathf.Abs(_stage.MaxChargeValue - 90f) < 1e-6f);

        // ---- 2. 抛竿改为鼠标左键（空手 + 钩子空闲）：无钓竿按钮，第一次点击 = 抛竿 ----
        Check("已无「钓竿」按钮（抛竿改由左键触发）",
            _stage.FindChild("CastButton", recursive: true, owned: false) == null);
        Check("初始：蓄力条隐藏", !_powerBar.Visible);

        ClickCast();
        Check("点击左键（空手 + 钩子空闲）后：蓄力条出现", _powerBar.Visible);
        Check("点击后：钩子仍空闲（等第二次点击抛出）", _hook.Current == Hook.State.Idle);
        var restCam = _camera.Position;   // 抛竿前记录的镜头位置（此后一竿结束镜头应回到这里）

        // ---- 2b. 右键取消抛竿：关闭蓄力条、退回 Ready（未钓鱼），钩子仍空闲可再抛 ----
        Check("蓄力中：处于「正在钓鱼」(GamePhase.Fishing)", _stage.Phase == Main.MainStage.GamePhase.Fishing);
        RightClick();
        Check("右键取消：蓄力条关闭", !_powerBar.Visible);
        Check("右键取消：已退出「正在钓鱼」退回 Ready", _stage.Phase == Main.MainStage.GamePhase.ReadyFishing);
        Check("右键取消：钩子仍空闲（可再抛竿）", _hook.Current == Hook.State.Idle);
        ClickCast();   // 重新蓄力（让第 3 节的第二次点击仍作为「抛出」而非再一次开始蓄力）

        // ---- 2c. 蓄力中点右上角「菜单」：先取消抛竿再打开菜单（等同先按右键）----
        Check("前置：蓄力中（Fishing）", _stage.Phase == Main.MainStage.GamePhase.Fishing);
        var menuBtn = _stage.GetNodeOrNull<Button>("UI/Hud/MenuUi/MenuButton");
        var menuPanel = _stage.GetNodeOrNull<Control>("UI/Hud/MenuUi/MenuPanel");
        if (menuBtn != null) menuBtn.EmitSignal(BaseButton.SignalName.Pressed);
        Check("点「菜单」：菜单已打开", menuPanel != null && menuPanel.Visible);
        Check("点「菜单」：先取消抛竿（蓄力条关 + 退回 Ready）",
            !_powerBar.Visible && _stage.Phase == Main.MainStage.GamePhase.ReadyFishing);
        if (menuBtn != null) menuBtn.EmitSignal(BaseButton.SignalName.Pressed);   // 关掉菜单，继续第 3 节
        ClickCast();   // 重新蓄力（让第 3 节的第二次点击仍作为「抛出」）

        // ---- 3. 再点左键 → 抛出（此刻蓄力值=0 → 应抛 MinSpeed 水平速度 + 上抛初速）----
        var home = _hook.GlobalPosition;

        ClickCast();   // 第二次点击 = 抛出
        Check("再点左键：抛出 → 蓄力条隐藏", !_powerBar.Visible);
        Check("再点左键：钩子进入飞行", _hook.Current == Hook.State.Flying);

        // 右上角实时速度标签：挂 HUD、初始文本含两个分量；手动刷新后文本跟随当前速度（尚未步进，速度=抛出初速）
        var speedLabel = Find("SpeedLabel") as Label;
        Check("右上角速度标签存在", speedLabel != null);
        _stage.UpdateSpeedLabel();
        Check("速度标签初始含「水平/垂直」分量",
            speedLabel != null && speedLabel.Text.Contains("水平") && speedLabel.Text.Contains("垂直"));
        var labelV = _hook.Velocity;   // 此刻尚未 Step，即抛出初速（水平=MinSpeed、垂直=上抛负值）
        _stage.UpdateSpeedLabel();
        Check($"速度标签刷新后显示当前 vx（水平分量={labelV.X}）",
            speedLabel != null && speedLabel.Text.Contains($"水平 {labelV.X:F1}"));
        Check($"速度标签刷新后显示当前 vy（垂直分量={labelV.Y}）",
            speedLabel != null && speedLabel.Text.Contains($"垂直 {labelV.Y:F1}"));

        // 初速 = (MinSpeed, 上抛 vy0)
        var v0 = _hook.Velocity;
        Check($"初速水平 = MinSpeed({_hook.MinSpeed})", Mathf.Abs(v0.X - _hook.MinSpeed) < 0.5f);
        Check("初速垂直 = 上抛（负值）", v0.Y < 0f);
        var vy0 = v0.Y;

        // ---- 4. 空中：先加重力 AirGravity×GravityBase×δt，再减空气阻力 AirResist×vy×δt/√Weight（g 恒定、重量只减阻力项） ----
        _hook.Step(dt);
        var afterGravity = vy0 + _hook.AirGravity * Hook.GravityBase * (float)dt;
        var expectedVy = afterGravity * (1f - _hook.AirResist * (float)dt / Mathf.Sqrt(_hook.Weight));
        Check("空中 1 帧：重力累加后减空气阻力（公式吻合）",
            Mathf.Abs(_hook.Velocity.Y - expectedVy) < 0.01f, $"实际 {_hook.Velocity.Y:F3} vs 预期 {expectedVy:F3}");
        Check("空中水平速度保持不变", Mathf.Abs(_hook.Velocity.X - _hook.MinSpeed) < 0.5f);

        // 飞行中 BeginReel 无效（收线仅落水后生效）
        _hook.BeginReel();
        Check("飞行中点击左键：不进入收线（仅落水后生效）", _hook.Current == Hook.State.Flying);

        // ---- 5. 镜头跟随：1 秒内 easeOut 居中，之后持续钉住 ----
        // 步进 30 帧（0.5s）：已完成 87.5% 缓动，应明显离开起点、大幅接近鱼钩
        var camFrom = _camera.Position;
        for (var i = 0; i < 30; i++) { _hook.Step(dt); _stage.StepCameraFollow(dt); }
        var hookMid = _hook.GlobalPosition;
        var camMid = _camera.Position;
        Check("0.5s：镜头已离开起点", (camMid - camFrom).Length() > 20f);
        Check("0.5s：镜头在向鱼钩收敛（比起点更近）",
            (camMid - hookMid).Length() < (camFrom - hookMid).Length());
        Check("0.5s：缓动尚未完全到位", (camMid - hookMid).Length() > 1f);

        // 再步进 30 帧（累计 1s）：缓动结束，镜头应精确钉住鱼钩当前位置
        for (var i = 0; i < 30; i++) { _hook.Step(dt); _stage.StepCameraFollow(dt); }
        Check("1s：镜头精确钉住鱼钩（居中）",
            (_camera.Position - _hook.GlobalPosition).Length() < 0.5f);

        // ---- 6. 触水入水：垂直迅速刹车到水中终端速度匀速下潜；水平受 WaterResist/√Weight 阻尼衰减到 0 ----
        StepUntil(dt, maxSteps: 400, stopWhen: () => _hook.Current != Hook.State.Flying);
        Check("触水后进入下沉", _hook.Current == Hook.State.Sinking, $"实际状态 {_hook.Current}");
        var entryY = _hook.GlobalPosition.Y;
        var entryVx = _hook.Velocity.X;
        var entryVy = _hook.Velocity.Y;
        var wrect = WaterRect(_stage);
        Check($"入水位置在水区上方区域内（x={_hook.GlobalPosition.X:F0} 在水区 x 范围）",
            wrect != null && (wrect.Value.HasPoint(_hook.GlobalPosition) || _hook.GlobalPosition.Y >= wrect.Value.Position.Y));

        var sinkTarget = _hook.AirGravity * Hook.GravityBase * Mathf.Sqrt(_hook.Weight) / _hook.WaterResist;   // 水中终端速度 = g×√Weight/WaterResist（动态计算：Weight/WaterResist 都可调）
        // 入水 0.5s（30 帧）：垂直应已从空中积攒的大 vy 大幅刹车（水中阻力大，dv/dt=900−8v 快速逼近终端）
        for (var i = 0; i < 30; i++) _hook.Step(dt);
        Check($"水中 0.5s：垂直已从入水速度 {entryVy:F0} 大幅刹车（< 入水 35%）",
            _hook.Velocity.Y < entryVy * 0.35f, $"实际 vy={_hook.Velocity.Y:F1}");
        Check("水中 0.5s：y 在缓慢下沉", _hook.GlobalPosition.Y > entryY + 1f);

        // 入水 1.5s（再 60 帧）：垂直收敛到终端速度并保持；水平按同源水中阻力衰减到 ≈ 入水值·e^(−(WaterResist/√Weight)·t)
        for (var i = 0; i < 60; i++) _hook.Step(dt);
        Check("水中 1.5s：垂直收敛到终端速度保持匀速",
            Mathf.Abs(_hook.Velocity.Y - sinkTarget) < 5f, $"实际 vy={_hook.Velocity.Y:F1} 目标={sinkTarget:F1}");
        var expectedVx = entryVx * Mathf.Exp(-(_hook.WaterResist / Mathf.Sqrt(_hook.Weight)) * 1.5f);
        Check($"水中 1.5s：水平按指数衰减 ≈ 入水值×e^(−(WaterResist/√Weight)·1.5)（{expectedVx:F1}）",
            Mathf.Abs(_hook.Velocity.X - expectedVx) < 2f, $"实际 vx={_hook.Velocity.X:F1}");

        // 入水 6s（再 270 帧）：水平按指数衰减的精确值 = 入水vx·e^(−(WaterResist/√Weight)·6)（重量越重阻尼越弱、漂停越慢）；
        // 垂直仍保持终端匀速（不被重力再加速）
        for (var i = 0; i < 270; i++) _hook.Step(dt);
        var expectedVx6 = entryVx * Mathf.Exp(-(_hook.WaterResist / Mathf.Sqrt(_hook.Weight)) * 6f);
        Check("水中 6s：水平按指数衰减精确吻合（重量越重漂停越慢）",
            Mathf.Abs(_hook.Velocity.X - expectedVx6) < 2f,
            $"实际 vx={_hook.Velocity.X:F2} vs 预期 {expectedVx6:F2}");
        Check("水中 6s：垂直保持终端匀速（不再加速）",
            Mathf.Abs(_hook.Velocity.Y - sinkTarget) < 0.5f, $"实际 vy={_hook.Velocity.Y:F2}");

        // ---- 7. 收线画圆弧：落水后点左键 → 线长锁定、受重力+鱼线约束荡到起点正下方停住 ----
        var anchor = home;   // 抛出起点
        var r0 = (_hook.GlobalPosition - anchor).Length();
        _hook.BeginReel();   // 模拟落水后的左键按下（MainStage._UnhandledInput 里触发）
        Check("落水后点击左键：进入收线模式（Reeling）",
            _hook.Current == Hook.State.Reeling, $"实际 {_hook.Current}");
        Check("线长锁定 = 点击瞬间到起点距离（钩子处在圆周上）",
            Mathf.Abs((_hook.GlobalPosition - anchor).Length() - r0) < 0.5f);

        var swingX0 = _hook.GlobalPosition.X;
        var swingY0 = _hook.GlobalPosition.Y;
        for (var i = 0; i < 60; i++) _hook.Step(dt);   // 放线摆动 1s
        Check("放线摆动：线长保持锁定半径不变（受鱼线约束画弧）",
            Mathf.Abs((_hook.GlobalPosition - anchor).Length() - r0) < 1f);
        Check("放线摆动：向左下方画弧（x 向起点正下方靠近、y 增大）",
            _hook.GlobalPosition.X < swingX0 && _hook.GlobalPosition.Y >= swingY0,
            $"实际位置 ({_hook.GlobalPosition.X:F0},{_hook.GlobalPosition.Y:F0}) 起点前 ({swingX0:F0},{swingY0:F0})");

        // 继续荡直到停住（水中阻尼令摆动收敛）；终点二选一：起点正下方，或弧线先碰到湖床
        StepUntil(dt, maxSteps: 2000, stopWhen: () => _hook.Parked);
        Check("摆动结束：停在稳定位置（Parked）", _hook.Parked, $"实际 Parked={_hook.Parked}");
        var parkPos = _hook.GlobalPosition;
        var parkFloor = WaterBody.SpaceOf(_stage.GetNodeOrNull<Area2D>("Water"))?.FloorYAt(parkPos.X);
        var atVerticalBottom = Mathf.Abs(parkPos.X - anchor.X) < 20f;
        var onLakebed = parkFloor.HasValue && Mathf.Abs(parkPos.Y - parkFloor.Value) < 1e-3f;
        Check("停住位置 = 起点正下方（x 对齐）或湖床（弧线先碰底）", atVerticalBottom || onLakebed,
            $"实际位置 {parkPos}（dx={parkPos.X - anchor.X:F1}，湖床 y={(parkFloor.HasValue ? parkFloor.Value.ToString("F1") : "无")}）");
        Check("停住：线长 = 锁定半径（钩子仍在锁定圆弧上）",
            Mathf.Abs((parkPos - anchor).Length() - r0) < 2f);
        Check("停住 ≠ 一竿结束：钩子仍忙（此时点击不会开始新抛竿）", _hook.IsBusy);
        Check("停住 ≠ 一竿结束：蓄力条隐藏（非蓄力中）", !_powerBar.Visible);

        // 停住后不按左键：原地不动（等收线）
        for (var i = 0; i < 30; i++) _hook.Step(dt);
        Check("停住后不按左键：钩子原地不动（等待收线）",
            (_hook.GlobalPosition - parkPos).Length() < 0.01f);

        // ---- 8. 按住左键收线：朝起点直线收回（不受重力），收完一竿结束、按钮恢复 ----
        var reelFrom = _hook.GlobalPosition;
        _hook.ReelHeld = true;
        _hook.Step(dt);
        var reelStep = (_hook.GlobalPosition - reelFrom).Length();
        Check("收线中：每帧位移 = ReelSpeed×dt（匀速直线，重力不参与加速）",
            Mathf.Abs(reelStep - _hook.ReelSpeed * (float)dt) < 0.5f, $"实际 {reelStep:F2}");
        Check("收线中：位移方向正对抛出起点（直线回拉）",
            (_hook.GlobalPosition - reelFrom).Normalized().Dot((anchor - reelFrom).Normalized()) > 0.999f);
        for (var i = 0; i < 29; i++) _hook.Step(dt);   // 累计收线 0.5s
        Check("收线中：钩子朝起点接近（距起点缩短）",
            (_hook.GlobalPosition - anchor).Length() < r0);
        // 注：park 落在「拉升区」（|dx|≤LiftUnlockRange）内收线是单摆（item 45 起的设计，横向走摆动公式），
        // 相对「收线起点→锚点」的直线本身就有几像素的横向偏离（此 park 实测 ~3.7px / 0.5s），
        // 因此阈值放宽到 8px（短程直线拉回仍是主导，偏离只是单摆的小幅弧线，绝无重力累加的垂降）。
        Check("收线中：钩子紧贴「收线起点→起点」的线（拉升区单摆允许小幅横向偏离）",
            Mathf.Abs((_hook.GlobalPosition - reelFrom).Cross((anchor - reelFrom).Normalized())) < 8f,
            $"横向偏离 {(float)Math.Abs((_hook.GlobalPosition - reelFrom).Cross((anchor - reelFrom).Normalized())):F2}px");
        Check("收线中：垂直速度 = 鱼线方向，不是重力累加（沉底也能直线拉离水底）",
            _hook.Velocity.Y < 0f, $"实际 vy={_hook.Velocity.Y:F1}");

        StepUntil(dt, maxSteps: 2000, stopWhen: () => _hook.Current == Hook.State.Rest);
        Check("收线收完（距起点<20）：一竿结束 Rest", _hook.Current == Hook.State.Rest, $"实际 {_hook.Current}");
        Check("收完位置 = 抛出起点附近",
            (_hook.GlobalPosition - home).Length() < Hook.ReelFinishDistance);
        Check("一竿结束后：钩子空闲（下次空手点击左键即可再抛竿）", !_hook.IsBusy);
        _hook.ReelHeld = false;

        // ---- 8.5 一竿结束 → 镜头从跟随位置 1 秒内平缓回到抛竿前记录的初始位置 ----
        Check("镜头回归已开始（Rest → Finished → OnHookFinished 置回归态）", _stage.CameraReturning);
        var startReturn = _camera.Position;   // 一竿结束时镜头所在（此前跟随鱼钩停住的位置）
        for (var i = 0; i < 30; i++) _stage.StepCameraFollow(dt);   // 回归 0.5s：easeOutCubic 已走 87.5%
        var midReturn = _camera.Position;
        Check("回归 0.5s：镜头已明显离开跟随起点", (midReturn - startReturn).Length() > 10f,
            $"位移 {(midReturn - startReturn).Length():F1}px");
        Check("回归 0.5s：大幅接近初始位置（是在往回收，不是停住）",
            (midReturn - restCam).Length() < (startReturn - restCam).Length());
        for (var i = 0; i < 40; i++) _stage.StepCameraFollow(dt);   // 再 0.667s → 总 1s+，缓动已到位
        Check("回归 ~1s：镜头精确回到抛竿前记录的初始位置",
            (_camera.Position - restCam).Length() < 1f, $"距初始 {(_camera.Position - restCam).Length():F2}px");
        Check("回归到位后：回归态结束（等待下次抛竿）", !_stage.CameraReturning);

        // ---- 9. 收线直线、松开左键立刻恢复重力（收线时不受重力；松手沿收紧后的线长圆弧摆回） ----
        _hook.Cast(0.3f);
        StepUntil(dt, maxSteps: 600, stopWhen: () => _hook.Current == Hook.State.Sinking);
        Check("前置：再抛一次入水（小力抛）", _hook.Current == Hook.State.Sinking, $"实际 {_hook.Current}");
        for (var i = 0; i < 30; i++) _hook.Step(dt);   // 入水 0.5s 刹车
        _hook.BeginReel();

        // 收线期间：朝起点匀速收进（每帧位移 = ReelSpeed×dt；深处直线 / 骑到 X 水面间隙保持高度水平移近，都只朝起点靠近）。
        // 精确的三档行为（直线段方向、骑线冻结高度、浅水/水上重力下降、拉升区解锁）在 TestFishWater.TestReelSurfaceTiers 单测；
        // 这里只断言集成层面的不变量：0.25s 内距起点显著缩短 + 若已进入容差带则深度必须合规。
        _hook.ReelHeld = true;
        var dBefore = (_hook.GlobalPosition - home).Length();
        for (var i = 0; i < 15; i++) _hook.Step(dt);   // 0.25s
        var dAfter = (_hook.GlobalPosition - home).Length();
        Check("收线：距起点明显缩短（0.25s 直线/骑线都收得回大半）",
            dBefore - dAfter > _hook.ReelSpeed * 0.25f * 0.75f, $"实际缩短 {dBefore - dAfter:F1}px");
        var waterSurfY = WaterBody.SpaceOf(_stage.GetNodeOrNull<Area2D>("Water"))?.SurfaceY;
        if (waterSurfY.HasValue)
        {
            var curGap = _hook.CurrentReelSurfaceGap;
            var curDepth = _hook.GlobalPosition.Y - waterSurfY.Value;
            if (curGap > 0f && curDepth >= curGap * 0.9f)   // 已进入容差带（骑线）：深度必须落在 [X×0.9, X×1.1] 内（太浅时还没进带 → 不触发）
                Check("收线骑线带：深度被钳在 [X×0.9, X×1.1]（不向深处直线、也不浮出带）",
                    curDepth <= curGap * 1.1f + 1f, $"深度 {curDepth:F1}px vs 带 [{curGap * 0.9f:F1},{curGap * 1.1f:F1}]");
            // 注：curGap == 0（main_stage 现配置 = 水面即分界线）时容差带塌缩成水面本身——
            // 水下直线收、水上空气圆弧，没有"骑线带"可断言，故跳过（X>0 的带行为仍由 TestFishing 逻辑与 TestFishWater.TestReelSurfaceTiers 覆盖）。
        }

        // 松开左键 → 立刻恢复重力：沿收紧后的线长圆弧往下摆（y 增大），线长保持锁定半径
        var rRelease = (_hook.GlobalPosition - home).Length();
        var releaseY = _hook.GlobalPosition.Y;
        _hook.ReelHeld = false;
        for (var i = 0; i < 20; i++) _hook.Step(dt);   // 0.333s 自由摆动
        Check("松手后：重力把钩子往下拽（y 增大）", _hook.GlobalPosition.Y > releaseY + 1f,
            $"松手 y={releaseY:F1} → {_hook.GlobalPosition.Y:F1}");
        Check("松手后：仍钉在收紧后的线长圆弧上（线长不变）",
            Mathf.Abs((_hook.GlobalPosition - home).Length() - rRelease) < 1f);

        // 默认 WaterResist=8 → 水中过阻尼，单摆不再摆回、沿弧渐近收向正下方——但大半径下渐近收尾很慢
        // （r≈660 要从 ~67° 蠕到 |x−锚点|<16px ≈ 50s+）。游戏里玩家马上会再按住收线不等它，
        // 这里验证「最终确实停住」：窗口放宽到 6000 帧（100s）覆盖收敛终值。
        StepUntil(dt, maxSteps: 6000, stopWhen: () => _hook.Parked);
        Check("松手后：沿圆弧荡到稳定位置停住（沉底或正下方）", _hook.Parked, $"实际 Parked={_hook.Parked}");
        Check("重停后：线长确实比收线前缩短（收线把线收短了）",
            (_hook.GlobalPosition - home).Length() < dBefore);

        // 再次收完 → Rest + 按钮恢复
        _hook.ReelHeld = true;
        StepUntil(dt, maxSteps: 3000, stopWhen: () => _hook.Current == Hook.State.Rest);
        Check("再次收线：收完一竿结束", _hook.Current == Hook.State.Rest, $"实际 {_hook.Current}");
        _hook.ReelHeld = false;

        // ---- 10. 满力直抛：水平速度 = MaxSpeed，空中重力参数生效 ----
        _hook.Cast(1.0f);
        Check("满力初速水平 = MaxSpeed", Mathf.Abs(_hook.Velocity.X - _hook.MaxSpeed) < 0.5f);
        var maxVx = _hook.Velocity.X;
        var maxVy0 = _hook.Velocity.Y;
        _hook.Step(dt);
        Check("满力：空中 vy 继续按重力累加", _hook.Velocity.Y > maxVy0);
        Check("满力：空中水平速度保持 MaxSpeed 不变", Mathf.Abs(_hook.Velocity.X - maxVx) < 0.5f);
        Check($"满力：水平速度确实远大于最小速度（{maxVx} >> {_hook.MinSpeed}）", maxVx > _hook.MinSpeed * 3);

        // ---- 11. 重量：g 恒定、重量只放大"阻力抗性"与水中目标速度（√Weight 递减曲线） ----
        var savedWeight = _hook.Weight;
        _hook.Weight = 4f;   // √4 = 2：空中阻力应减半、水中终端速度 = 900×√4/WaterResist = 225、水平阻尼应为 WaterResist/2

        Check("重量 4 > 1：√Weight 作用曲线成立（√4=2）", Mathf.Sqrt(_hook.Weight) == 2f);

        // 空中同初速对照：单帧后 vy 与纯自由落体（无阻力）参考值的偏差应更小——重量 4 阻力减半 → 更接近自由落体轨迹
        _hook.Cast(0f);
        var wVy0 = _hook.Velocity.Y;
        var wFreeFall = wVy0 + _hook.AirGravity * Hook.GravityBase * (float)dt;   // 无阻力的 vy（理论最"快下落"基准）
        var wExpectedVy1 = wFreeFall * (1f - _hook.AirResist * (float)dt);        // Weight=1：减原阻力
        var wExpectedVy4 = wFreeFall * (1f - _hook.AirResist * (float)dt / 2f);   // Weight=4：阻力减半
        _hook.Step(dt);
        Check("重量 4：空中 1 帧按减半阻力公式吻合",
            Mathf.Abs(_hook.Velocity.Y - wExpectedVy4) < 0.01f, $"实际 {_hook.Velocity.Y:F3} vs 预期 {wExpectedVy4:F3}");
        Check("重量 4：空中阻力减半 → 更接近自由落体（比重量 1 偏差小）",
            Mathf.Abs(_hook.Velocity.Y - wFreeFall) < Mathf.Abs(wExpectedVy1 - wFreeFall),
            $"重量4 偏差 {Mathf.Abs(_hook.Velocity.Y - wFreeFall):F3} vs 重量1 偏差 {Mathf.Abs(wExpectedVy1 - wFreeFall):F3}");

        // 入水后：水中终端速度 = g×√4/WaterResist = 225；水平阻尼 = WaterResist/√4
        StepUntil(dt, maxSteps: 400, stopWhen: () => _hook.Current != Hook.State.Flying);
        Check("重量 4：仍正常触水下沉", _hook.Current == Hook.State.Sinking, $"实际 {_hook.Current}");
        var wEntryVx = _hook.Velocity.X;
        for (var i = 0; i < 90; i++) _hook.Step(dt);   // 入水 1.5s：收敛完成
        var wSinkTarget = _hook.AirGravity * Hook.GravityBase * 2f / _hook.WaterResist;   // 重量4：终端 = 900×√4/8 = 225
        Check("重量 4：水中收敛到终端 = g×√4/WaterResist 并保持",
            Mathf.Abs(_hook.Velocity.Y - wSinkTarget) < 5f, $"实际 vy={_hook.Velocity.Y:F1} 目标={wSinkTarget:F1}");
        var wExpVx = wEntryVx * Mathf.Exp(-(_hook.WaterResist / 2f) * 1.5f);
        Check($"重量 4：水平阻尼 = WaterResist/2（按 e^(−4·1.5) 衰减 ≈ {wExpVx:F1}）",
            Mathf.Abs(_hook.Velocity.X - wExpVx) < 2f, $"实际 vx={_hook.Velocity.X:F1}");

        // 场景重量的 √ 作用曲线：无论配重 >1（增重下沉更快）还是 <1（轻钩下沉更慢），
        // √Weight 都落在 1 与 Weight 之间（递减收益，非线性，两侧都成立）
        var sqrtScene = Mathf.Sqrt(savedWeight);
        var diminishing = savedWeight > 1f
            ? sqrtScene > 1f && sqrtScene < savedWeight          // 增重侧：(1, W)
            : savedWeight < 1f
                ? sqrtScene < 1f && sqrtScene > savedWeight      // 轻钩侧：(W, 1)
                : Mathf.Abs(sqrtScene - 1f) < 1e-6f;             // Weight = 1：√1 = 1
        Check($"场景重量 {savedWeight}：√{savedWeight} 落在 (1, {savedWeight}) 之间（递减收益）",
            diminishing,
            $"实际 √{savedWeight}={sqrtScene:F3}");
        _hook.Weight = savedWeight;   // 收尾还原

        // 回收上一竿（重量测试遗留的沉水钩）到一竿结束，得到「钓竿按钮已恢复」的干净基线
        StepUntil(dt, maxSteps: 4000, stopWhen: () => _hook.Current != Hook.State.Sinking);
        _hook.ReelHeld = true;
        StepUntil(dt, maxSteps: 3000, stopWhen: () => _hook.Current == Hook.State.Rest);
        _hook.ReelHeld = false;
        Check("前置：上一竿已收完（钩子空闲，可再抛竿）", !_hook.IsBusy);

        // ---- 12. 不点左键：垂沉到水底停住（一竿不算结束），全程钩子保持忙，随后按住左键直接收线收完 ----
        // 走完整点击流程：抛竿（第一次点）→ 抛出（第二次点）→ 沉到底（无需重新甩杆）→ 收线（收完恢复空闲）
        ClickCast();
        Check("纯垂沉：点击左键 → 蓄力条出现", _powerBar.Visible);
        ClickCast();   // 第二次 = 抛出
        Check("纯垂沉：抛出后蓄力条隐藏", !_powerBar.Visible);
        Check("纯垂沉：进入飞行", _hook.Current == Hook.State.Flying, $"实际 {_hook.Current}");
        StepUntil(dt, maxSteps: 600, stopWhen: () => _hook.Current == Hook.State.Sinking);
        Check("纯垂沉：进入下沉", _hook.Current == Hook.State.Sinking, $"实际 {_hook.Current}");
        var waterSpace = WaterBody.SpaceOf(_stage.GetNodeOrNull<Area2D>("Water"));
        StepUntil(dt, maxSteps: 5000, stopWhen: () => _hook.Current != Hook.State.Sinking);
        Check("纯垂沉：沉到水底 → 停在水底（进入停住态，一竿未结束）",
            _hook.Current == Hook.State.Reeling && _hook.Parked,
            $"实际 {_hook.Current} Parked={_hook.Parked}");
        Check("纯垂沉：停在水底边缘（湖床折线）",
            waterSpace != null && Mathf.Abs(_hook.GlobalPosition.Y - waterSpace.FloorYAt(_hook.GlobalPosition.X)) < 1f,
            $"实际 y={_hook.GlobalPosition.Y:F1} 期望 {waterSpace?.FloorYAt(_hook.GlobalPosition.X):F1}");
        Check("停在水底：钩子仍忙（不需要重新甩杆，直接收线即可）", _hook.IsBusy);

        // 无需重新甩杆：按住左键直接收线收完 → 一竿结束 + 钩子恢复空闲
        _hook.ReelHeld = true;
        StepUntil(dt, maxSteps: 3000, stopWhen: () => _hook.Current == Hook.State.Rest);
        Check("沉底后按住左键收线：收完一竿结束（Rest）", _hook.Current == Hook.State.Rest, $"实际 {_hook.Current}");
        Check("收完：钩子空闲（下次空手点击左键即可再抛竿）", !_hook.IsBusy);
        _hook.ReelHeld = false;

        // ---- 13. 鱼线视觉（Line2D）：6px 连续细线；纯函数曲度规则 + 顶点前笔直/顶点后上拱/收线迅速转直 ----
        // 13.1 纯函数：目标曲度与弧线采样（maxSag/sagFallSpeed 由调用方注入）
        Check("TargetSag：收线/停住 → 0（笔直）", FishingLineCurve.TargetSag(999f, taut: true, maxSag: 36f, sagFallSpeed: 450f) == 0f);
        Check("TargetSag：vy≤0 或参考速度为 0 → 0（顶点前不下坠 / 永不弯曲）",
            FishingLineCurve.TargetSag(-100f, taut: false, maxSag: 36f, sagFallSpeed: 450f) == 0f
            && FishingLineCurve.TargetSag(0f, taut: false, maxSag: 36f, sagFallSpeed: 450f) == 0f
            && FishingLineCurve.TargetSag(900f, taut: false, maxSag: 36f, sagFallSpeed: 0f) == 0f);
        Check("TargetSag：vy≥参考速度 → maxSag",
            Mathf.Abs(FishingLineCurve.TargetSag(900f, taut: false, maxSag: 36f, sagFallSpeed: 450f) - 36f) < 1e-4f);
        Check("PeakProfile：p=0.5 时正中峰值 = 1（等效旧二次贝塞尔拱）",
            Mathf.Abs(FishingLineCurve.PeakProfile(0.5f, 0.5f) - 1f) < 1e-5f);
        Check("PeakProfile：峰值精确落在 t=peak（=1、谷 0 夹住峰）",
            Mathf.Abs(FishingLineCurve.PeakProfile(0.75f, 0.75f) - 1f) < 1e-5f);
        Check("PeakProfile：p=0.75 时中点偏离低于峰值（≈0.889，拱顶偏鱼钩侧）",
            Mathf.Abs(FishingLineCurve.PeakProfile(0.5f, 0.75f) - (0.6666667f * 1.3333333f)) < 1e-5f);
        Check("PeakProfile：端点 = 0（首尾贴住钓竿/鱼钩）",
            Mathf.Abs(FishingLineCurve.PeakProfile(0f, 0.75f)) < 1e-6f && Mathf.Abs(FishingLineCurve.PeakProfile(1f, 0.75f)) < 1e-6f);
        var clampedProbe = FishingLineCurve.PeakProfile(0.5f, 0f);   // peak 超界钳到 [0.05,0.95]，不除零不 NaN
        Check("PeakProfile：peak=0 被钳位、结果有界非 NaN", clampedProbe <= 1f && clampedProbe >= -1f && !float.IsNaN(clampedProbe));

        var sam = FishingLineCurve.Samples(Vector2.Zero, new Vector2(400, 200), 40f, 0.75f);
        Check("Samples：弧线端点恒等于两端点", sam.Length >= 2 && sam[0] == Vector2.Zero && sam[^1] == new Vector2(400, 200));
        Check("Samples：sag=0 → 2 点直线", FishingLineCurve.Samples(Vector2.Zero, new Vector2(400, 200), 0f, 0.75f).Length == 2);
        var devPeak = ChordDeviation(sam, Vector2.Zero, new Vector2(400, 200), 0.75f);
        var devMid = ChordDeviation(sam, Vector2.Zero, new Vector2(400, 200), 0.5f);
        Check("Samples：t≈0.75（鱼钩侧）处偏离 ≈ 满拱 sag(40)", Mathf.Abs(devPeak - 40f) < 2f, $"实际 {devPeak:F1}");
        Check("Samples：中点偏离明显小于峰值（拱顶已偏鱼钩侧）", devMid < 0.95f * devPeak, $"中 {devMid:F1} 峰 {devPeak:F1}");

        // 13.2 集成：Line2D 接线
        var line = _stage.GetNodeOrNull<Line2D>("FishingLine");
        Check("场景有 FishingLine 节点（Line2D）", line != null);
        if (line == null) return;
        Check("鱼线粗细 = 6px（连续细线）", Mathf.Abs(line.Width - 6f) < 1e-5f, $"实际 Width={line.Width}");
        Check("导出：LineMaxSag > 0（最大拱高，手感调参不硬编码）", _stage.LineMaxSag > 0f, $"实际 {_stage.LineMaxSag}");
        Check("导出：LineSagFallSpeed > 0 / LineSagResponse > 0（拱高下落与响应速度，手感调参不硬编码）",
            _stage.LineSagFallSpeed > 0f && _stage.LineSagResponse > 0f,
            $"实际 {_stage.LineSagFallSpeed} / {_stage.LineSagResponse}");
        Check("导出：LineSagPeak ∈ (0.5, 1]（拱顶偏鱼钩端，极限 1 = 完全贴钩）",
            _stage.LineSagPeak > 0.5f && _stage.LineSagPeak <= 1f, $"实际 {_stage.LineSagPeak}");
        _stage.UpdateFishingLine(dt);
        Check("空闲（一竿结束钩子在起点）：鱼线隐藏", !line.Visible);

        _hook.Cast(0.5f);
        for (var i = 0; i < 10; i++) _hook.Step(dt);   // 飞行初段：仍在上升（vy<0）→ 曲度目标 0 → 笔直
        _stage.UpdateFishingLine(dt);
        Check("抛竿后：鱼线显示", line.Visible);
        Check("飞行初段（钩子仍在上升）：线是笔直 2 点直线", line.Points.Length == 2, $"Points.Length={line.Points.Length}");
        Check("线两端 = 钓竿 CastOrigin 与鱼钩当前位置",
            line.Points.Length >= 2 && line.Points[0] == _hook.CastOrigin && line.Points[^1] == _hook.GlobalPosition);

        // 飞到钩子开始下落（vy>0，过顶点）：再下坠 0.17s，让拱在响应速度(240px/s)下充分长出，再断言上拱
        StepUntil(dt, maxSteps: 300, stopWhen: () => _hook.Velocity.Y > 0f);
        Check("前置：钩子已过顶点开始下落", _hook.Velocity.Y > 0f, $"vy={_hook.Velocity.Y:F1}");
        for (var i = 0; i < 10; i++) { _hook.Step(dt); _stage.UpdateFishingLine(dt); }
        var maxDev = line.Points.Length > 2 ? MaxDeviation(line.Points, _hook.CastOrigin, _hook.GlobalPosition) : 0f;
        Check("下落中：线中段高于弦（上拱、有曲度）", line.Points.Length > 2 && maxDev > 1f,
            $"Points.Length={line.Points.Length}, 最高偏离={maxDev:F2}");

        // 落水 → 收线：线在 ~0.15s 内迅速转为笔直
        StepUntil(dt, maxSteps: 400, stopWhen: () => _hook.Current == Hook.State.Sinking);
        _hook.BeginReel();
        Check("前置：进入收线模式", _hook.Current == Hook.State.Reeling, $"实际 {_hook.Current}");
        for (var i = 0; i < 15; i++) { _hook.Step(dt); _stage.UpdateFishingLine(dt); }   // 0.25s 持续推进
        Check("收线 ~0.25s 后：线转为笔直（曲度已收完）", line.Points.Length == 2, $"Points.Length={line.Points.Length}");

        // ---- 13.3 拉力 → 线色（纯函数）：<70 纯白；70..100 线性由白渐变到红；≥100 全红 ----
        var c1 = Main.MainStage.LineTensionColor(0f, 70f, 100f);
        Check("拉力 0 → 纯白（不预警）", c1 == Colors.White, $"实际 {c1}");
        var c69 = Main.MainStage.LineTensionColor(69.9f, 70f, 100f);
        Check("拉力 69.9（<70）→ 仍是纯白", c69 == Colors.White, $"实际 {c69}");
        var c85 = Main.MainStage.LineTensionColor(85f, 70f, 100f);
        Check("拉力 85（70..100 中点）→ 半红（R=1、G=B≈0.5，线性）",
            Mathf.Abs(c85.R - 1f) < 1e-4f && Mathf.Abs(c85.G - 0.5f) < 1e-4f && Mathf.Abs(c85.B - 0.5f) < 1e-3f,
            $"实际 {c85}");
        var c100 = Main.MainStage.LineTensionColor(100f, 70f, 100f);
        Check("拉力 100 → 全红", c100.R == 1f && c100.G == 0f && c100.B == 0f, $"实际 {c100}");
        Check("拉力 >100 → 钳在全红（不会溢出变色）",
            Main.MainStage.LineTensionColor(150f, 70f, 100f) == c100);
        Check("退化：at ≤ from → 一律纯白（不启用预警）",
            Main.MainStage.LineTensionColor(999f, 100f, 80f) == Colors.White);

        // ---- 13.4 集成：数值机涨拉力 → UpdateFishingLine 把线色刷对应（真实 Hook.Struggle） ----
        var lineColorLow = line.DefaultColor;
        _hook.Struggle.BeginFight(100, 100, 100, maxTension: 200);
        _hook.Struggle.Step(dt, holding: true);   // 涨拉力（+40/s 施力）
        _hook.Struggle.Step(dt, holding: true);
        _hook.Struggle.Step(dt, holding: true);   // 0.05s → 拉力 ≈ 2 —— 仍 <70 → 纯白
        _stage.UpdateFishingLine(dt);
        Check("集成：拉力低（≈2）→ 线仍纯白", line.DefaultColor == Colors.White, $"实际 {line.DefaultColor}");
        for (var i = 0; i < 130; i++) _hook.Struggle.Step(dt, holding: true);   // 再 2.17s → 拉力 ≈ 88.7（70..100 内）
        _stage.UpdateFishingLine(dt);
        var hot = line.DefaultColor;
        Check("集成：拉力 ≈89 → 线明显变红（红分量满、绿蓝衰减）",
            Mathf.Abs(hot.R - 1f) < 1e-3f && hot.G < 0.6f && hot.G > 0.2f && Mathf.Abs(hot.G - hot.B) < 1e-3f,
            $"拉力 {_hook.Struggle.Tension:F1}，颜色 {hot}");
        _hook.Struggle.BeginFight(100, 100, 100, maxTension: 200);   // 复位数值机（拉力归零）
        _stage.UpdateFishingLine(dt);
        Check("集成：拉力归零 → 线回纯白", line.DefaultColor == Colors.White, $"实际 {line.DefaultColor}");
    }

    /// <summary>以固定步长驱动钩子直到条件满足或超步数（每步 delta 秒）。</summary>
    private void StepUntil(double dt, int maxSteps, Func<bool> stopWhen)
    {
        for (var i = 0; i < maxSteps && !stopWhen(); i++) _hook.Step(dt);
    }

    /// <summary>折线采样在参数 t 处的偏离量（相对弦线，向上拱 = 正值）：取最近采样点算。</summary>
    private static float ChordDeviation(Vector2[] points, Vector2 a, Vector2 b, float t)
    {
        var last = points.Length - 1;
        var idx = Mathf.Clamp((int)Mathf.Round(t * last), 0, last);
        var chord = a.Lerp(b, idx / (float)last);
        return chord.Y - points[idx].Y;
    }

    /// <summary>折线相对弦线的最大竖直偏离（拱高）：采样由 Samples 生成（均匀 t=i/segments），
    /// 对照弦线在同一参数处的 Y 量竖直间距——不沿弦投影，弦越陡投影越失真（只能测出 sag·cos²θ）。</summary>
    private static float MaxDeviation(Vector2[] points, Vector2 a, Vector2 b)
    {
        var last = points.Length - 1;
        if (last < 1) return 0f;
        var max = 0f;
        for (var i = 0; i < points.Length; i++)
        {
            var chordY = a.Y + (b.Y - a.Y) * (i / (float)last);
            max = Mathf.Max(max, chordY - points[i].Y);
        }
        return max;
    }

    /// <summary>合成一次空手左键点击（落在世界坐标、避开宝箱图标），直接派给主关卡 _UnhandledInput
    /// ——与真实输入同一条路径（真实鼠标坐标是屏幕空间 = 画布变换×世界坐标，MainStage 内部会换算还原）。</summary>
    private void ClickCast()
    {
        if (_hook == null) return;
        var worldPos = _hook.CastOrigin + new Vector2(120f, 60f);   // 抛出起点右下方，不落在宝箱/蓄力条上
        var screenPos = _stage.GetViewport().GetCanvasTransform() * worldPos;
        var ev = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = screenPos,
            GlobalPosition = screenPos,
        };
        _stage._UnhandledInput(ev);
    }

    /// <summary>合成一次右键点击（蓄力中取消抛竿用），派给主关卡 _UnhandledInput（与 ClickCast 同路径）。</summary>
    private void RightClick()
    {
        if (_hook == null) return;
        var worldPos = _hook.CastOrigin + new Vector2(200f, 100f);
        var screenPos = _stage.GetViewport().GetCanvasTransform() * worldPos;
        var ev = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = screenPos,
            GlobalPosition = screenPos,
        };
        _stage._UnhandledInput(ev);
    }

    private void Check(string name, bool cond, string? detail = null)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
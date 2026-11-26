using System.Linq;
using Godot;
using SaleFish.Common;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.Save;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 主关卡交互集成测试：实例化正式场景 scenes/main_stage.tscn（Storage / RandomPool 导出随场景走，
/// 与正式运行完全一致）。仓库开关 = 点击常显的宝箱 Sprite2D「Inventory」（挂 WarehouseButton 组件）：
/// 直接向节点派发按钮 pressed 信号（headless 下不经过输入队列）、直接调用 _UnhandledInput 派发合成鼠标事件、
/// 直接向视图派发 _GuiInput（拖拽/拾起用），逐条验证需求里的行为：
///  ▪ 空手初始态：仅「获取放置物」可用，丢弃不出现；宝箱图标可见（常显，不随仓库隐藏）；
///  ▪ 已无「打开仓库/隐藏仓库」按钮；
///  ▪ 获取后：手持、「获取」禁用、「丢弃」出现；手持时再点「获取」无效（物品保持原实例）；
///  ▪ 点击宝箱：未打开 → 创建并显示仓库；再点 → 隐藏；再点 → 恢复**同一视图实例**且内容保留
///    （标识物品仍在原位——区分销毁重建，RemoveStorage 会清空容器）；
///  ▪ 图标全程可见（需求：图片不用再隐藏）；
///  ▪ 丢弃：回到初始空手态。
///  ▪ 空手拖拽整仓（规则 1/2/3）：点放置物拾起；手持时不拖；空手按下空格子 → 整仓跟随光标、松手停、
///    关闭后下次打开仍在拖后位置（生成中心点跟随）。
///  ▪ 钓上自动开仓（绑定按钮 AutoOpenWarehouseButtonPath）：钓上 → 物品到鼠标 + 绑定的仓库自动打开；
///    路径置空/无效 → 不自动打开（只生成物品）。
/// 注：FindChildren 的 type 参数对 C# 剧本类不可靠（ClassDB 不注册裸类名），统一按运行期类型 is 过滤。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestMainStage.tscn --quit-after 240
/// </summary>
public sealed partial class TestMainStage : Node
{
    private int _passed;
    private int _failed;

    private Main.MainStage _stage = null!;
    private Sprite2D _inventorySprite = null!;
    private Button _exitFishButton = null!;
    /// <summary>蓄力条：可见 = 处于蓄力/抛竿（验证「仓库打开时外部点击不误触发抛竿」的探针）。</summary>
    private ProgressBar? _powerBar;

    public override void _Ready()
    {
        // 开局阶段现在随存档走（SaveManager.InFreeMove，默认 true = 自由移动）。
        Check("默认开局：SaveManager.InFreeMove 默认 true（直接运行主关卡 = 自由移动）", SaveManager.InFreeMove);
        // 存量用例从「准备钓鱼」起步（等价「读了一个当时坐在椅子上的存档」），与新开局区分开。
        SaveManager.InFreeMove = false;

        _stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn")
            .Instantiate<Main.MainStage>();
        AddChild(_stage);

        _inventorySprite = _stage.GetNodeOrNull<Sprite2D>("FishKeep");
        _exitFishButton = FindButton("ExitFishingButton");
        _powerBar = _stage.FindChild("PowerBar", recursive: true, owned: false) as ProgressBar;
        if (_powerBar == null) GD.PrintErr("[MainStageTest] 找不到 PowerBar 蓄力条");

        Run();
        GD.Print($"[MainStageTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[MainStageTest] ALL PASSED" : "[MainStageTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>按名字找按钮（运行时代码创建的节点不被场景“拥有”，FindChild 需 owned=false）。</summary>
    private Button FindButton(string name)
    {
        var n = _stage.FindChild(name, recursive: true, owned: false);
        if (n is not Button b)
        {
            GD.PrintErr($"[MainStageTest] 找不到按钮 {name}");
            GetTree().Quit(1);
            return null!;   // 不会执行到：quit 已排队，仅满足编译器
        }
        return b;
    }

    /// <summary>让全局手拿一件物品（主关卡已无「获取」按钮：直接经 autoload 手注入，等价「钓上/获取」后的手持态）。</summary>
    private static void TakeHeldItem() =>
        HeldItemController.Instance?.TakeExternal(
            new PlacementItemTypeDef { DisplayName = "测试", ShapePattern = new[] { "X" } }.ToItem(), null, 52);

    /// <summary>清空全局手持（主关卡已无「丢弃」按钮：直接 DiscardHeld）。</summary>
    private static void DropHeld() => HeldItemController.Instance?.DiscardHeld();

    /// <summary>FreeMove 下左键点击钓鱼点（合成世界坐标点击，派给 _UnhandledInput）。</summary>
    private void ClickSpot()
    {
        var spot = _stage.GetNodeOrNull<FishingSpot>("FishingSpot");
        if (spot != null) ClickWorld(spot.GlobalPosition);
    }

    private void Run()
    {
        var hand = HeldItemController.Instance;
        Check("全局手 autoload 存在", hand != null);
        Check("宝箱图标存在且初始可见", _inventorySprite != null && _inventorySprite.Visible);
        Check("已无「打开仓库」按钮", _stage.FindChild("OpenButton", recursive: true, owned: false) == null);
        Check("已无「隐藏仓库」按钮", _stage.FindChild("HideButton", recursive: true, owned: false) == null);
        Check("主关卡已无「获取放置物/丢弃放置物」按钮（已移到 InventoryDemo）",
            _stage.FindChild("GetButton", recursive: true, owned: false) == null
            && _stage.FindChild("DiscardButton", recursive: true, owned: false) == null);
        Check("「退出钓鱼」按钮存在", _exitFishButton != null);
        Check("初始（Ready）：「退出钓鱼」显示", _exitFishButton != null && _exitFishButton.Visible);

        // 主关卡不再有获取按钮：让全局手拿一件物品（autoload 全局手，直接注入等价「钓上/获取」后的手持态）
        TakeHeldItem();
        var held = hand!.HeldItem;
        Check("手持一件物品", held != null);

        // 点击宝箱 → 打开仓库（首次创建）
        ClickSprite();
        var created = FindWarehouse(visible: true);
        Check("点击宝箱：仓库已创建并显示", created != null);
        Check("点击宝箱：格子由配置决定", created != null && created.Inventory.Size.X > 0);
        Check("宝箱图标仍可见（图片不随仓库隐藏）", _inventorySprite is { Visible: true });

        // 放入一件标识物品：验证「隐藏只藏不毁」——销毁重建会清空容器，隐藏再显示则数据保留
        var marker = new InventoryItem(PlacementShape.Rectangle(1, 1));
        Check("仓库可正常放入物品", created != null
            && created.Inventory.TryPlace(marker, new Vector2I(2, 0)).Outcome == PlaceOutcome.Success);

        // 再点宝箱 → 隐藏仓库
        ClickSprite();
        Check("再点宝箱：仓库已隐藏", FindWarehouse(visible: true) == null);
        Check("再点宝箱后：图标仍可见（无需隐藏图片）", _inventorySprite is { Visible: true });

        // 三击宝箱 → 重新显示：同一视图实例、内容保留
        ClickSprite();
        var reopened = FindWarehouse(visible: true);
        Check("三击宝箱：仓库恢复显示（同一视图实例）",
            reopened != null && ReferenceEquals(reopened, created));
        Check("三击宝箱：内容保留（标识物品仍在 (2,0)）",
            reopened != null && ReferenceEquals(reopened.Inventory.GetItemAt(new Vector2I(2, 0)), marker));

        // 清空手持（主关卡无丢弃按钮：直接 DiscardHeld）；仓库保持打开状态
        DropHeld();
        Check("清空手持后：空手", hand.HeldItem == null);
        Check("仓库状态不受影响（仍打开）", FindWarehouse(visible: true) != null);

        AssertOutsideClickRules(ref hand);
        TestCatchAutoOpenAndDrag();
        TestCatchFirstOpenEndToEnd();
        TestFishingPhaseLocksWarehouse();
        TestShopTeleportAndFishSpot();
        TestMouseWorldWithCamera();
        TestPlayerPositionRoundTrip();
        TestLoadKeepsWarehousesClosed();
    }

    /// <summary>读档不恢复仓库的开关状态：存档时开着的箱子，读档后一律是关闭的（内容/位置照旧）。</summary>
    private void TestLoadKeepsWarehousesClosed()
    {
        const string slot = "tst_open_warehouses";
        var keep = _stage.GetNodeOrNull<WarehouseButton>("FishKeep");
        Check("读档关箱前置：鱼护按钮存在", keep != null);
        if (keep == null) return;

        keep.Open();
        Check("存档前鱼护是打开的", keep.IsOpen);
        SaveManager.SaveNow(slot);
        SaveManager.LoadSlot(slot);
        SaveManager.ApplyPendingWarehouses();
        Check("读档后鱼护关闭（开关状态不保存）", !keep.IsOpen);
        Check("读档后所有仓库都关闭", WarehouseButton.All.All(b => !b.IsOpen));
        SaveService.Delete(slot);
    }

    /// <summary>玩家位置随存档保存 / 读档恢复：位置写进快照；读档先暂存，主关卡 _Ready 摆到玩家身上
    /// （镜头一并横移到玩家 X，Ready 阶段镜头不跟随玩家，否则一进场看不到人）。</summary>
    private void TestPlayerPositionRoundTrip()
    {
        const string slot = "tst_pos_mainstage";

        // ---- 1. 保存：主关卡回写的位置 → 快照 → 落盘 ----
        SaveManager.PlayerPosition = new Vector2(432.5f, -12f);
        var snapshot = SaveManager.BuildSnapshot();
        Check("快照记录玩家位置", snapshot.PlayerX == 432.5f && snapshot.PlayerY == -12f);
        SaveService.Save(slot, snapshot);

        // ---- 2. 读档：位置进暂存区，只取一次 ----
        SaveManager.PlayerPosition = null;
        Check("读档成功", SaveManager.LoadSlot(slot));
        var pending = SaveManager.ConsumePendingPlayerPosition();
        Check("读档：玩家位置进入暂存区", pending is { } p && p.DistanceTo(new Vector2(432.5f, -12f)) < 0.01f);
        Check("暂存位置取一次即清空（不会重复摆）", SaveManager.ConsumePendingPlayerPosition() == null);
        SaveService.Delete(slot);

        // ---- 3. 进主关卡：_Ready 把暂存位置摆到玩家身上 ----
        SaveManager.Apply(new SaveFile { InFreeMove = true, PlayerX = 900f, PlayerY = 100f });
        var stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn").Instantiate<Main.MainStage>();
        AddChild(stage);
        var player = stage.GetNodeOrNull<CharacterBody2D>("Player");
        var camera = stage.GetNodeOrNull<Camera2D>("Camera2D");
        Check("读档进场：玩家回到存档位置",
            player != null && player.GlobalPosition.DistanceTo(new Vector2(900f, 100f)) < 0.5f);
        Check("读档进场：镜头横移到玩家 X", camera != null && Mathf.Abs(camera.Position.X - 900f) < 0.5f);
        Check("进场后位置回写到 SaveManager（下次保存带当前位置）",
            SaveManager.PlayerPosition is { } live && live.DistanceTo(new Vector2(900f, 100f)) < 0.5f);
        Check("没有暂存位置（新游戏 / 直接运行场景）→ 保持场景默认出生点",
            SaveManager.ConsumePendingPlayerPosition() == null);
        stage.QueueFree();
    }

    /// <summary>仓库模态规则：打开仓库时的「仓库页面以外」左键点击行为（详见 MainStage 类文档）——
    /// 单仓库+空手 → 关闭当前仓库且不抛竿；单仓库+手持 → 不关闭、物品保持；多仓库打开 → 外部点击一律不生效、
    /// 不抛竿，仅宝箱按钮仍可隐藏它所对应的仓库。蓄力条可见性作为「是否误触发抛竿」的探针。</summary>
    private void AssertOutsideClickRules(ref HeldItemController? hand)
    {
        // ---- 1) 单仓库 + 空手：外部点击关闭当前仓库，且不误触发抛竿（蓄力条保持隐藏） ----
        Check("外部点击前提：单仓库打开", WarehouseRegistry.OpenCount == 1);
        ClickOutside();
        Check("外部点击（空手+单仓库）：关闭当前仓库", FindWarehouse(visible: true) == null);
        Check("外部点击（空手+单仓库）：不触发抛竿（蓄力条隐藏）", _powerBar != null && !_powerBar.Visible);

        // 重新打开仓库，进入「手持 + 外部点击」段落
        ClickSprite();
        Check("外部点击测试后：宝箱可重新打开仓库", FindWarehouse(visible: true) != null);

        // ---- 2) 单仓库 + 手持放置物：外部点击不关闭仓库、不抛竿，物品保持在手 ----
        TakeHeldItem();
        var heldOutside = hand!.HeldItem;
        Check("手持物品（用于外部点击测试）", heldOutside != null);
        ClickOutside();
        Check("外部点击（手持+单仓库）：不关闭仓库", FindWarehouse(visible: true) != null);
        Check("外部点击（手持+单仓库）：不抛竿且物品保持",
            (hand!.HeldItem != null && ReferenceEquals(hand.HeldItem, heldOutside)) && !_powerBar!.Visible);

        // 手持时宝箱开关仍可作为合法关闭入口（该物品来自「获取」，无来源仓库 → 隐藏不会被迫放回，保持手持）
        ClickSprite();
        Check("外部点击测试后：宝箱仍可关闭仓库（手持时）",
            FindWarehouse(visible: true) == null && ReferenceEquals(hand.HeldItem, heldOutside));
        DropHeld();
        ClickSprite();
        Check("多仓库前提：已重新打开单仓库", FindWarehouse(visible: true) != null && WarehouseRegistry.OpenCount == 1);

        // ---- 3) 多仓库同时打开：外部点击一律不生效（不关闭、不抛竿），仅宝箱可隐藏所对应的仓库 ----
        var host = new Control();
        _stage.AddChild(host);
        var secondDef = new StorageTypeDef
        {
            Id = 951,
            DisplayName = "测试乙仓",
            CellSize = 52,
            ValidPattern = new[] { "####", "####" },
        };
        var second = InventoryView.CreateStorage(host, secondDef, new Vector2(1500, 600));
        Check("多仓库：第二座仓库已打开（OpenCount=2）", second != null && WarehouseRegistry.OpenCount == 2);

        ClickOutside();
        Check("多仓库：外部点击不关闭任何仓库", WarehouseRegistry.OpenCount == 2);
        Check("多仓库：外部点击不抛竿（蓄力条隐藏）", _powerBar != null && !_powerBar.Visible);

        // 宝箱按钮仍能隐藏它所对应的仓库（MainStage 自己的 _warehouse），第二座不受影响
        ClickSprite();
        Check("多仓库：宝箱仍可隐藏对应仓库",
            WarehouseRegistry.OpenCount == 1 && FindWarehouse(visible: true) is { } kept
            && ReferenceEquals(kept, second) && second.Visible);

        // 清理：销毁第二座仓库（测试结束即退出，仅为保持登记表干净）
        if (second != null) InventoryView.RemoveStorage(second);
        host.QueueFree();
    }

    /// <summary>
    /// 空手拖拽整仓 + 钓上自动开仓（两条新需求一起测，共享仓库场景）：
    ///  ▪ 拖拽前置：仓库开着、空手（入口 = 本段之外已把上一段的手丢弃、仓库隐藏中）；
    ///  ▪ 规则 1：空手点到放置物 → 拾起；
    ///  ▪ 手持时点空格 → 不拖（StartDrag 直接拒绝，位置不变）；
    ///  ▪ 规则 3：空手按下空格子 → StartDrag，MoveDragTo 让被按点钉在光标下（整仓跟随），松手停；
    ///  ▪ 规则 2：单仓库 + 空手点仓库外 → 关闭；
    ///  ▪ 下次打开：同一实例、仍在拖后位置（打开中心随拖拽记忆）；
    ///  ▪ 钓上自动开仓：默认绑定 Inventory → 钓上物品到鼠标 + 仓库自动打开；仓库已开时不重复不关闭；
    ///    路径置空/无效 → 只生成物品、不自动打开。
    /// 注：拖拽后重开/关闭均经按钮组件（btn.Open/Close）直接驱动，避免拖拽后面板可能盖住宝箱导致点击路由变化，
    /// 路径与宝箱点击完全一致（Toggle → Open/Close）。规则 2 的「点仓库外关闭」仍需真实外部点击（ClickOutside，与面板无交集）。
    /// </summary>
    private void TestCatchAutoOpenAndDrag()
    {
        var hand = HeldItemController.Instance!;

        // ---- A. 钓上自动开仓：绑定导出 + 解析 ----
        Check("钓上自动开仓：默认绑定鱼护（FishKeep）按钮", _stage.AutoOpenWarehouseButtonPath?.ToString() == "FishKeep");
        var btn = _stage.GetNodeOrNull<WarehouseButton>("FishKeep");
        Check("钓上自动开仓：绑定解析到仓库按钮组件（Inventory 挂了 WarehouseButton）", btn != null);
        if (btn == null) return;

        // 打开仓库准备拖拽测试（宝箱点击路径，等价用户开仓；锚点位置已被前序用例证明不挡宝箱）
        ClickSprite();
        var w = FindWarehouse(visible: true);
        Check("拖拽前置：仓库已打开", w != null);
        if (w == null) return;

        // ---- 前置：视图交互区已覆盖到背景盒（Bug-2 回归：背景盒边缘不再被当成「仓库外」） ----
        var boxGrab = new Vector2(820, 640);   // 视图本地：主背包背景盒右下边缘（格子区 (81..753,159..579) 以外）
        Check("规则3·视图覆盖到背景盒（点盒子边缘不落入关闭逻辑）", w != null && w.GetRect().HasPoint(boxGrab));

        // ---- B. 规则 1：空手点到放置物 → 拾起 ----
        var marker = w!.Inventory.GetItemAt(new Vector2I(2, 0));   // 前序「标识物品」留下的 1×1
        Check("拖拽前置：(2,0) 有标识物品", marker != null);
        ClickWarehouseCell(w, new Vector2I(2, 0));
        Check("规则1·空手点击放置物→拾起（不拖拽）",
            hand.HeldItem != null && ReferenceEquals(hand.HeldItem, marker));

        // ---- C. 手持时点空格 → 不进入拖拽（拖拽只在空手生效） ----
        var before = w.Position;
        w.StartDrag(boxGrab);                      // 手持时 StartDrag 应直接拒绝
        w.MoveDragTo(new Vector2(900, 500));
        Check("手持时点空格：不触发拖拽（位置不变、仍手持）",
            w.Position == before && hand.HeldItem != null);
        hand.DiscardHeld();                        // 空手，进入拖拽测试

        // ---- D1. 规则 3 入口分流：空手点空白格 → 不拾起（走拖拽入口），松手复位 ----
        var cellGrab = w.CellToPixel(new Vector2I(4, 4)) + new Vector2(w.CellSize / 2f, w.CellSize / 2f);
        ClickWarehousePixel(w, cellGrab);
        Check("规则3·空手点空白格：不拾起（同拖拽入口）", hand.HeldItem == null);
        ReleaseWarehouse(w);

        // ---- D2. 规则 3：空手按住仓库背景（盒子边缘）→ 整仓跟随光标，松手即停 ----
        ClickWarehousePixel(w, boxGrab);           // 按下不松开：进拖拽而非拾起/关闭
        Check("规则3·空手点仓库背景：不拾起、不关仓（仍空手）", hand.HeldItem == null);

        var c1 = new Vector2(600, 800);
        w.MoveDragTo(c1);
        Check("规则3·拖拽跟随①：被按点钉在光标（整仓整体平移）",
            (w.GetGlobalRect().Position + boxGrab - c1).Length() < 0.01f);
        var c2 = new Vector2(900, 1000);
        w.MoveDragTo(c2);
        Check("规则3·拖拽跟随②：移动到新光标", (w.GetGlobalRect().Position + boxGrab - c2).Length() < 0.01f);
        var draggedOrigin = w.GetGlobalRect().Position;

        ReleaseWarehouse(w);
        w.MoveDragTo(new Vector2(300, 400));
        Check("规则3·松手后停止跟随（位置不再变化）", w.GetGlobalRect().Position == draggedOrigin);

        // ---- E. 规则 2：单仓库 + 空手点仓库外 → 关闭当前仓库 ----
        ClickOutside();
        Check("规则2·点仓库外关闭当前仓库", FindWarehouse(visible: true) == null);

        // ---- F. 下次打开：同一实例、仍在拖后位置（生成中心点跟随记忆） ----
        btn.Open();                                    // 与宝箱点击同路径（Toggle → Open）
        var reopened = FindWarehouse(visible: true);
        Check("下次打开：同一视图实例（内容保留）", reopened != null && ReferenceEquals(reopened, w));
        Check("下次打开：仍在拖后位置", reopened != null && (reopened.GetGlobalRect().Position - draggedOrigin).Length() < 0.01f);

        // ---- G. 钓上自动开仓（绑定按钮驱动） ----
        var spawner = _stage.GetNodeOrNull<FishSpawner>("FishSpawner");
        Fish? fish = null;
        if (spawner != null)
            foreach (var n in spawner.GetChildren())
                if (n is Fish f) { fish = f; break; }
        Check("钓上自动开仓前置：场景鱼带背包形态（ItemDef）", fish != null && fish.Def?.ItemDef != null);
        if (fish == null) return;

        // G1 仓库已开时钓上：不重复新建、不关闭（幂等）
        _stage.OnHookCaught(fish);
        Check("钓上·仓库已开时：仍保持打开且不重复（仅一座）",
            WarehouseRegistry.OpenCount == 1 && FindWarehouse(visible: true) != null);
        Check("钓上·物品到鼠标（鱼的背包装入，占 2 格）", hand.HeldItem?.Shape.CellCount == 2, hand.HeldItem == null ? "空手" : $"格数={hand.HeldItem.Shape.CellCount}");
        hand.DiscardHeld();

        // G2 默认路径钓上 → 自动打开 + 仍在拖后位置
        btn.Close();
        Check("钓上前置：仓库已关闭", FindWarehouse(visible: true) == null);
        _stage.OnHookCaught(fish);
        var byCatch = FindWarehouse(visible: true);
        Check("钓上·自动打开绑定的仓库（OpenCount 0→1）",
            WarehouseRegistry.OpenCount == 1 && byCatch != null);
        Check("钓上自动打开：仍在拖后位置", byCatch != null && (byCatch.GetGlobalRect().Position - draggedOrigin).Length() < 0.01f);
        Check("钓上·物品到鼠标", hand.HeldItem != null);
        hand.DiscardHeld();

        // G3 路径置空 → 钓上只生成物品、不自动打开（保持旧行为）
        _stage.AutoOpenWarehouseButtonPath = null;
        btn.Close();
        _stage.OnHookCaught(fish);
        Check("钓上·路径置空：不自动打开仓库", FindWarehouse(visible: true) == null && WarehouseRegistry.OpenCount == 0);
        Check("钓上·路径置空：物品仍生成到鼠标", hand.HeldItem != null);
        hand.DiscardHeld();

        // G4 路径无效 → 同样不自动打开
        _stage.AutoOpenWarehouseButtonPath = new NodePath("Nonexistent");
        _stage.OnHookCaught(fish);
        Check("钓上·路径无效：不自动打开仓库", FindWarehouse(visible: true) == null);
        Check("钓上·路径无效：物品仍生成到鼠标", hand.HeldItem != null);
        hand.DiscardHeld();

        // G5 恢复默认（保持可复现）
        _stage.AutoOpenWarehouseButtonPath = new NodePath("FishKeep");

        // ---- H. 重复开合回归：几何（格子偏移/视图尺寸）不随开合次数漂移，且偶数次重开后盒面背景仍可拖 ----
        var geo0 = (grid: w.GridOffset, size: w.Size);
        for (var i = 0; i < 4; i++)   // 连续 4 次重开（覆盖第 2/3/4 次），几何必须与首开一致
        {
            btn.Close();
            btn.Open();
            var ro = FindWarehouse(visible: true);
            Check($"重复开合第 {i + 2} 次：GridOffset 与首开一致（不向左上漂移）",
                ro != null && ro.GridOffset == geo0.grid,
                ro == null ? "未打开" : $"期望 {geo0.grid} 实际 {ro.GridOffset}");
            Check($"重复开合第 {i + 2} 次：视图尺寸与首开一致",
                ro != null && ro.Size == geo0.size,
                ro == null ? "未打开" : $"期望 {geo0.size} 实际 {ro.Size}");
        }
        // 偶数次重开后：盒面背景照旧可拖（不被上次几何漂移破坏）
        ClickWarehousePixel(w, boxGrab);
        var targetH = w.GetGlobalRect().Position + boxGrab + new Vector2(50, 30);
        w.MoveDragTo(targetH);
        Check("重复开合后：盒面背景仍可拖拽（被按点钉在光标）",
            (w.GetGlobalRect().Position + boxGrab - targetH).Length() < 0.01f);
        ReleaseWarehouse(w);
    }

    /// <summary>
    /// Bug-1 回归：从未打开过仓库 + 钓上时相机已随鱼钩走远 → 自动打开的仓库仍按锚点（_Ready 相机休息位）落位，
    /// 不会因为把世界锚点经「当前相机」画布变换而偏到屏幕外最左侧。新开一个主关卡、把镜头拉到远离锚点的钓点、
    /// 再直接钓上，验证首次自动打开的位置在屏幕内（不偏出左缘）。
    /// </summary>
    private void TestCatchFirstOpenEndToEnd()
    {
        var stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn").Instantiate<Main.MainStage>();
        AddChild(stage);
        stage.ProcessMode = ProcessModeEnum.Disabled;
        var hook = stage.GetNodeOrNull<Hook>("Hook");
        if (hook != null) hook.ProcessMode = ProcessModeEnum.Disabled;
        var spawner = stage.GetNodeOrNull<FishSpawner>("FishSpawner");
        if (spawner != null) spawner.ProcessMode = ProcessModeEnum.Disabled;

        Fish? fish = null;
        if (spawner != null)
            foreach (var n in spawner.GetChildren())
                if (n is Fish f) { fish = f; break; }
        Check("首次钓上自动开仓前置：新场景鱼带背包形态", fish != null && fish.Def?.ItemDef != null);
        if (fish == null) { stage.Free(); return; }

        // 镜头拉到远离锚点的钓点（真实钓上时镜头正跟随钩子走远）——复现 Bug 1 的关键前置
        var cam = stage.GetNodeOrNull<Camera2D>("Camera2D");
        if (cam != null) cam.Position = new Vector2(6000, 0);

        var beforeOpen = WarehouseRegistry.OpenCount;
        stage.OnHookCaught(fish);   // 从未打开过 → 首次即自动打开

        InventoryView? fresh = null;
        foreach (var n in stage.FindChildren("*", recursive: true, owned: false))
            if (n is InventoryView v && v.Visible) { fresh = v; break; }
        Check("首次钓上自动开仓：仓库已打开", fresh != null && WarehouseRegistry.OpenCount == beforeOpen + 1);
        var px = fresh?.GetGlobalRect().Position ?? new Vector2(float.NaN, float.NaN);
        Check("首次钓上自动开仓：相机已走远仍按锚点落位（不偏出屏幕左缘）",
            px.X > -1000f, $"实际 X={px.X:F1}");
        HeldItemController.Instance?.DiscardHeld();
        stage.Free();   // 立即释放（同步测试不跨帧，避免残留这座关卡的打开中仓库干扰后续断言）
    }

    /// <summary>
    /// 钓鱼阶段状态机（GamePhase.Ready / Fishing）：开始蓄力 → 正在钓鱼，钓上或一竿结束 → 退回准备钓鱼；
    /// 正在钓鱼期间点击仓库按钮被吞掉（不开仓库、不打断蓄力）；其余时间仓库按钮正常开关。
    /// 阶段迁移本身用公开的 OnHookCaught / OnHookFinished 直接驱动（真实事件链由 TestHookup/TestFishing 覆盖）。
    /// </summary>
    private void TestFishingPhaseLocksWarehouse()
    {
        var hand = HeldItemController.Instance!;
        var btn = _stage.GetNodeOrNull<WarehouseButton>("FishKeep");
        Check("状态机前置：初始为「准备钓鱼」", btn != null && _stage.Phase == Main.MainStage.GamePhase.Ready);
        if (btn == null) return;
        hand.DiscardHeld();
        btn.Close();   // 清理前一个用例留下的打开中仓库，从「仓库关着、空手」的干净状态开始

        // ---- 1) 蓄力开始 → 进入「正在钓鱼」----
        ClickWorld(new Vector2(600, 200));   // 空手点世界空白区（不在宝箱上）→ 开始蓄力
        Check("蓄力开始：阶段进入「正在钓鱼」", _stage.Phase == Main.MainStage.GamePhase.Fishing);
        Check("蓄力开始：进度条出现", _powerBar != null && _powerBar.Visible);

        // ---- 2) 正在钓鱼：点仓库按钮被吞掉——不开仓库、不打断蓄力 ----
        ClickSprite();
        Check("正在钓鱼点仓库按钮：不开仓库（仍关闭）", FindWarehouse(visible: true) == null);
        Check("正在钓鱼点仓库按钮：不打断蓄力（进度条仍在）", _powerBar != null && _powerBar.Visible);
        Check("正在钓鱼点仓库按钮：阶段仍为「正在钓鱼」", _stage.Phase == Main.MainStage.GamePhase.Fishing);

        // ---- 3) 一竿结束（未钓上）→ 退回「准备钓鱼」，仓库按钮恢复 ----
        _stage.OnHookFinished();
        Check("一竿结束（未钓上）：阶段退回「准备钓鱼」", _stage.Phase == Main.MainStage.GamePhase.Ready);
        ClickSprite();
        Check("一竿结束后：仓库按钮恢复正常（能打开仓库）", FindWarehouse(visible: true) != null);
        btn.Close();

        // ---- 4) 钓上 → 退回「准备钓鱼」+ 绑定仓库自动打开 ----
        ClickWorld(new Vector2(600, 200));   // 再蓄力（第 3 步 OnHookFinished 已清蓄力态）
        Check("再蓄力：阶段进入「正在钓鱼」", _stage.Phase == Main.MainStage.GamePhase.Fishing);
        Fish? fish = null;
        var spawner = _stage.GetNodeOrNull<FishSpawner>("FishSpawner");
        if (spawner != null)
            foreach (var n in spawner.GetChildren())
                if (n is Fish f) { fish = f; break; }
        Check("钓上退出前置：场景鱼带背包形态", fish != null && fish.Def?.ItemDef != null);
        if (fish == null) return;
        _stage.OnHookCaught(fish);
        Check("钓上：阶段退回「准备钓鱼」", _stage.Phase == Main.MainStage.GamePhase.Ready);
        Check("钓上：绑定仓库自动打开（已非钓鱼状态可开）", FindWarehouse(visible: true) != null);
        hand.DiscardHeld();
        btn.Close();
    }

    /// <summary>
    /// 「退出钓鱼」→ 传送到鱼店（准备开店）：
    ///  ▪ 玩家 + 鱼护 一起到鱼店，阶段 = PrepareShop（不再切自由移动）；
    ///  ▪ 准备开店阶段玩家**不能移动**（A/D 与走向都不生效），要点椅子也不生效
    ///    —— 只有 FreeMove 能走（卖完鱼后进入，见 TestShop 的后半段）。
    /// </summary>
    private void TestShopTeleportAndFishSpot()
    {
        var player = _stage.GetNodeOrNull<CharacterBody2D>("Player");
        var spot = _stage.GetNodeOrNull<FishingSpot>("FishingSpot");
        // 悬浮提示是 prefab 实例（Scenes/SitFishTooltip.tscn），摆在 UI/Hud 下。
        var tip = _stage.GetNodeOrNull<SitFishTooltip>("UI/Hud/SitFishTooltip");
        var shop = _stage.GetNodeOrNull<InteractZone>("FishShop");
        var keep = _stage.GetNodeOrNull<Sprite2D>("FishKeep");
        var shopSpawn = _stage.GetNodeOrNull<Node2D>("ShopSpawn");
        var keepAtShop = _stage.GetNodeOrNull<Node2D>("FishKeepAtShop");
        Check("准备开店前置：Player / FishingSpot / 悬浮提示 / 鱼店 / 鱼护 / 两个落点 均存在",
            player != null && spot != null && tip != null && shop != null && keep != null
            && shopSpawn != null && keepAtShop != null);
        if (player == null || spot == null) return;

        Check("准备开店前置：处于 Ready", _stage.Phase == Main.MainStage.GamePhase.Ready);
        Press(_exitFishButton);
        Check("点「退出钓鱼」：进入准备开店（PrepareShop）", _stage.Phase == Main.MainStage.GamePhase.PrepareShop);
        Check("准备开店状态写入存档标记（SaveManager.Phase = PrepareShop）", SaveManager.Phase == SavePhase.PrepareShop);
        Check("传送到鱼店：玩家在鱼店落点",
            shopSpawn != null && player.GlobalPosition.DistanceTo(shopSpawn.GlobalPosition) < 2f);
        Check("传送：鱼护搬到鱼店旁",
            keep != null && keepAtShop != null && keep.GlobalPosition.DistanceTo(keepAtShop.GlobalPosition) < 2f);
        Check("准备开店：「退出钓鱼」按钮隐藏", !_exitFishButton.Visible);

        // ---- 准备开店阶段：玩家不能移动（StepPlayerMovement 不产生水平位移；只受重力落地）----
        var beforeX = player.GlobalPosition.X;
        for (var i = 0; i < 30; i++) _stage.StepPlayerMovement(1.0 / 60.0);
        Check("准备开店：玩家不会水平移动（A/D 无效）",
            Mathf.Abs(player.GlobalPosition.X - beforeX) < 1f);

        // ---- 准备开店阶段：点椅子无效、也不亮高亮/不弹提示 ----
        player.GlobalPosition = new Vector2(spot.GlobalPosition.X + 40f, player.GlobalPosition.Y);   // 强制放到椅子旁
        ClickSpot();
        Check("准备开店点椅子：不触发（阶段不变）", _stage.Phase == Main.MainStage.GamePhase.PrepareShop);
        _stage.UpdateFishSpotUi(spot.GlobalPosition);
        var highlight = spot.GetNodeOrNull<Line2D>("Highlight");
        Check("准备开店：钓鱼点高亮关闭", highlight != null && !highlight.Visible);
        if (tip != null) Check("准备开店：不显示「坐下钓鱼」提示", !tip.Visible);
    }

    /// <summary>
    /// 坐标换算回归用例：相机不在原点时，屏幕坐标必须经「画布变换取逆」才是世界坐标。
    /// 旧 bug = 直接把 Viewport.GetMousePosition（屏幕坐标）当世界坐标 → 屏幕左上角被误判落在钓鱼点检测框里、
    /// 「坐下钓鱼」提示显示到椅子上。这里用「相机偏移 + 屏幕坐标」直接验证换算与提示定位。
    /// </summary>
    private void TestMouseWorldWithCamera()
    {
        var camera = _stage.GetNodeOrNull<Camera2D>("Camera2D");
        var spot = _stage.GetNodeOrNull<FishingSpot>("FishingSpot");
        var tip = _stage.GetNodeOrNull<SitFishTooltip>("UI/Hud/SitFishTooltip");
        Check("坐标换算前置：Camera2D / FishingSpot / 悬浮提示(prefab) 均存在",
            camera != null && spot != null && tip != null);
        if (camera == null || spot == null || tip == null) return;

        var viewport = _stage.GetViewport();
        var half = viewport.GetVisibleRect().Size / 2f;
        var savedCamera = camera.Position;
        camera.Position = new Vector2(1200f, 400f);   // 故意把相机挪离原点
        camera.ForceUpdateScroll();

        // Drag Center 锚点：屏幕中心 = 相机位置。
        var centerWorld = MouseWorld.FromScreen(viewport, half);
        Check("屏幕中心换算成世界 = 相机位置", centerWorld.DistanceTo(new Vector2(1200f, 400f)) < 2f);

        // 屏幕左上角 = 相机位置 - 半个视野，绝不是 (0,0)。
        var cornerWorld = MouseWorld.FromScreen(viewport, Vector2.Zero);
        Check("屏幕左上角换算成世界 = 相机位置 - 半视野",
            cornerWorld.DistanceTo(new Vector2(1200f, 400f) - half) < 2f);
        Check("屏幕左上角不再被当成世界原点 (0,0)", cornerWorld.DistanceTo(Vector2.Zero) > 100f);
        Check("相机偏移时：屏幕左上角不在钓鱼点检测框内（旧 bug 会判在里面）", !spot.IsPointInside(cornerWorld));

        // 悬浮提示定位：给世界坐标 → 屏幕位置应等于该世界点经画布变换的位置（相机偏移下仍贴着该点）。
        var mouseWorld = spot.GlobalPosition;
        tip.ShowAt(mouseWorld);
        var expectedScreen = viewport.GetCanvasTransform() * mouseWorld;
        Check("悬浮提示左上角 = 该世界点的屏幕位置（相机偏移下仍贴合）",
            tip.GlobalPosition.DistanceTo(expectedScreen) < 2f);

        camera.Position = savedCamera;
        camera.ForceUpdateScroll();
        tip.Hide();
    }

    /// <summary>合成一次落在世界坐标（画布变换后即屏幕坐标）的空手左键点击，直接派给主关卡 _UnhandledInput
    /// （与真实输入同路径：世界坐标 → 屏幕坐标，MainStage 内部再换算还原）。</summary>
    private void ClickWorld(Vector2 worldPos)
    {
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

    /// <summary>派发一次落在仓库某格中心的左键按下（视图本地坐标，等价真实引擎把点击交给 _gui_input）。</summary>
    private static void ClickWarehouseCell(InventoryView view, Vector2I cell)
    {
        var half = view.CellSize / 2f;
        ClickWarehousePixel(view, view.CellToPixel(cell) + new Vector2(half, half));
    }

    /// <summary>派发一次落在仓库视图指定像素处的左键按下（视图本地坐标）。</summary>
    private static void ClickWarehousePixel(InventoryView view, Vector2 pixel)
    {
        var evt = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pixel,
            GlobalPosition = pixel,
        };
        view._GuiInput(evt);
    }

    /// <summary>派发一次左键松开（拖拽结束用）。</summary>
    private static void ReleaseWarehouse(InventoryView view)
    {
        var evt = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
            Position = Vector2.Zero,
            GlobalPosition = Vector2.Zero,
        };
        view._GuiInput(evt);
    }

    /// <summary>派发按钮 pressed 信号（等价真实点击，headless 下不经过输入队列）。</summary>
    private static void Press(Button b) => b.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>
    /// 合成一次落在宝箱图标中心、且与真实鼠标坐标一致的左键点击，直接派给主关卡的 _UnhandledInput
    /// （headless 下无输入系统路由）。真实输入的鼠标坐标是屏幕空间（= 画布变换×世界坐标），
    /// 而 MainStage 内部会用 canvasTransform⁻¹ 还原世界坐标，所以这里必须先把世界坐标转到屏幕坐标，
    /// 否则 Camera2D 偏移（场景里是 (300,0)）会导致点击落空。
    /// </summary>
    private void ClickSprite()
    {
        if (_inventorySprite == null) return;   // 缺失时由前序断言暴露
        var worldPos = _inventorySprite.ToGlobal(_inventorySprite.GetRect().GetCenter());
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

    /// <summary>
    /// 合成一次落在「仓库页面以外」的左键点击，直接派给主关卡的 _UnhandledInput（headless 下无输入系统路由）。
    /// 选屏幕左下角：远离宝箱图标（世界中上部，屏幕坐标约 (1005,644)）、仓库面板（世界锚点 (0,0) 附近 →
    /// 屏幕中部）、工具栏（左上角 (16,18)）与仓库网格，必不命中它们，等价真实游戏里点在空白世界区。
    /// </summary>
    private void ClickOutside()
    {
        var ev = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = new Vector2(24f, 950f),
            GlobalPosition = new Vector2(24f, 950f),
        };
        _stage._UnhandledInput(ev);
    }

    private void Check(string name, bool cond) => Check(name, cond, null);

    private void Check(string name, bool cond, string? detail)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }

    /// <summary>场景树中可见/不可见的仓库视图（FindChildren type 参数对 C# 类不可靠，改用运行期 is 过滤）。</summary>
    private InventoryView? FindWarehouse(bool visible)
    {
        foreach (var n in _stage.FindChildren("*", "", recursive: true, owned: false))
            if (n is InventoryView v && v.Visible == visible) return v;
        return null;
    }
}
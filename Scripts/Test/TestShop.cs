using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SaleFish.Common;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.Save;
using SaleFish.Shop;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 卖鱼测试：一段纯逻辑（SellService 的偏好/预算/折扣/确定性），一段主关卡端到端
/// （Ready →「退出钓鱼」→ 准备开店 → 价格板/未标价警告/鱼护确认 → 结算 → 自由移动）。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestShop.tscn --quit-after 300
/// </summary>
public sealed partial class TestShop : Node
{
    private int _passed;
    private int _failed;

    private Main.MainStage _stage = null!;
    private readonly string _slot = "tst_shop_" + Guid.NewGuid().ToString("N")[..8];

    private static readonly FishTagDef DeliciousTag = new() { Id = 11, DisplayName = "美味" };

    public override void _Ready()
    {
        TestSellServiceRules();
        TestStorageOptions();
        TestShopFlow();
        SaveService.Delete(_slot);
        SaveManager.ShopPrices.Clear();
        GD.Print($"[ShopTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[ShopTest] ALL PASSED" : "[ShopTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    // ---------- 1. 纯逻辑 ----------

    private void TestSellServiceRules()
    {
        // 折扣概率曲线：p = ((0.7A − P) / 0.7A)²
        Check("折扣概率：标价 = 70% 实际价值 → 0", Mathf.IsZeroApprox(SellService.DiscountChance(70, 100)));
        Check("折扣概率：标价 = 实际价值 → 0", Mathf.IsZeroApprox(SellService.DiscountChance(100, 100)));
        Check("折扣概率：标价 0 → 100%", Mathf.IsEqualApprox(SellService.DiscountChance(0, 100), 1f));
        Check("折扣概率：标价 35（50% 实际价值）→ 25%", Mathf.IsEqualApprox(SellService.DiscountChance(35, 100), 0.25f));
        Check("折扣概率：实际价值 ≤ 0 → 0（忘填价值的兜底）", Mathf.IsZeroApprox(SellService.DiscountChance(10, 0)));

        // 种子派生：定价/货架一变就变，同货同价不变
        var fishA = MakeFish(1, DeliciousTag);
        var fishB = MakeFish(2);
        var lots = new List<SellService.Lot> { MakeLot(fishA, 50, 100), MakeLot(fishB, 80, 100) };
        var seed1 = SellService.DeriveSeed(lots);
        Check("种子：相同货架+定价 → 相同种子", seed1 == SellService.DeriveSeed(lots.ToList()));
        var repriced = new List<SellService.Lot> { MakeLot(fishA, 51, 100), MakeLot(fishB, 80, 100) };
        Check("种子：改一处定价 → 种子变化", seed1 != SellService.DeriveSeed(repriced));

        // 偏好顾客：标价 ≤ 实际价值才买；预算内尽量多买
        var chef = MakeCustomer("厨师", 150, 150, DeliciousTag);
        var lotsCheap = new List<SellService.Lot> { MakeLot(fishA, 100, 100), MakeLot(fishA, 100, 100) };
        var r1 = SellService.Run(lotsCheap, new[] { chef }, 1, 1, 42);
        Check("偏好顾客：预算 150、单价 100 → 只买 1 条", r1.Customers.Count == 1 && r1.Customers[0].Bought.Count == 1);
        Check("偏好顾客：不超预算", r1.TotalIncome == 100 && r1.Customers[0].Budget == 150);
        Check("偏好顾客：收入/剩余统计", r1.SoldCount[fishA.Id] == 1 && r1.RemainingCount[fishA.Id] == 1);

        // 偏好顾客 + 标价高于实际价值 → 不买
        var overpriced = new List<SellService.Lot> { MakeLot(fishA, 120, 100) };
        var r2 = SellService.Run(overpriced, new[] { chef }, 1, 1, 7);
        Check("偏好顾客：标价 > 实际价值 → 不买", r2.TotalIncome == 0 && r2.Customers[0].Bought.Count == 0);

        // 非偏好顾客：标价 = 实际价值（无折扣）→ 不买
        var ranger = MakeCustomer("游侠", 1000, 1000);
        var noDiscount = new List<SellService.Lot> { MakeLot(fishB, 100, 100) };
        var r3 = SellService.Run(noDiscount, new[] { ranger }, 1, 1, 11);
        Check("非偏好顾客：没有折扣 → 不买", r3.TotalIncome == 0);

        // 非偏好顾客：标价 0（100% 折扣概率）→ 一定买
        var free = new List<SellService.Lot> { MakeLot(fishB, 0, 100) };
        var r4 = SellService.Run(free, new[] { ranger }, 1, 1, 11);
        Check("非偏好顾客：标价 0 → 100% 概率买下", r4.TotalIncome == 0 && r4.SoldCount[fishB.Id] == 1);

        // 确定性：同 seed 同货 → 完全相同的结算
        var many = new List<SellService.Lot>
        {
            MakeLot(fishA, 40, 100), MakeLot(fishA, 40, 100), MakeLot(fishB, 60, 100),
        };
        var pool = new[]
        {
            MakeCustomer("厨师", 100, 300, DeliciousTag),
            MakeCustomer("游侠", 100, 300),
        };
        var seedA = SellService.DeriveSeed(many);
        var x1 = SellService.Run(many, pool, 1, 3, seedA);
        var x2 = SellService.Run(many, pool, 1, 3, seedA);
        Check("确定性：同 seed 两次结算结果一致",
            x1.TotalIncome == x2.TotalIncome && x1.SoldCount.Count == x2.SoldCount.Count
            && x1.Customers.Count == x2.Customers.Count);
        Check("确定性：结算记录的种子 = 传入种子", x1.Seed == seedA);
        Check("图鉴最高价数据齐全", x1.BestPrice.All(kv => kv.Value >= 0));
    }

    // ---------- 2. 仓库配置项（能否拖动 / 关闭按钮） ----------

    private void TestStorageOptions()
    {
        Check("StorageTypeDef.CanDrag 默认 false（不可拖）", !new StorageTypeDef().CanDrag);
        var warehouseDef = ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/Warehouse1.tres");
        var keepDef = ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/MainBag.tres");
        Check("鱼店仓库：CanDrag = false（面板固定）", warehouseDef != null && !warehouseDef.CanDrag);
        Check("鱼护：CanDrag = true（保留原来的整仓拖动）", keepDef != null && keepDef.CanDrag);

        // 关闭按钮：配了贴图才生成；点它 = 关闭该仓库；CanDrag=false 时拖不动
        var tex = ResourceLoader.Load<Texture2D>("res://Assets/InventoryImage/Label.png");
        var def = new StorageTypeDef
        {
            Id = 9999,
            DisplayName = "关闭按钮测试仓",
            CellSize = 40,
            ValidPattern = ["xx", "xx"],
            CanDrag = false,
            CloseButtonTexture = tex,
            CloseButtonPosition = new Vector2(8, 8),
            CloseButtonSize = new Vector2(32, 32),
        };
        var host = new Control();
        AddChild(host);
        var view = InventoryView.CreateStorage(host, def, new Vector2(10, 20));
        Check("配了关闭按钮贴图 → 生成关闭按钮",
            view != null && view.GetNodeOrNull<TextureButton>("CloseButton") != null);
        if (view != null)
        {
            var before = view.Position;
            view.StartDrag(Vector2.Zero);
            view.MoveDragTo(new Vector2(600, 600));
            Check("CanDrag=false：整仓拖动无效", view.Position == before);
            view.GetNodeOrNull<TextureButton>("CloseButton")?.EmitSignal(BaseButton.SignalName.Pressed);
            Check("点关闭按钮 → 该仓库暂时关闭（隐藏）", !view.Visible);
            view.Hide();   // 确保注销登记，避免影响后续用例
        }
        host.QueueFree();

        var plain = new StorageTypeDef { Id = 9998, DisplayName = "无关闭按钮", CellSize = 40, ValidPattern = ["x"] };
        var host2 = new Control();
        AddChild(host2);
        var view2 = InventoryView.CreateStorage(host2, plain, Vector2.Zero);
        Check("没配关闭按钮贴图 → 不生成关闭按钮", view2 != null && view2.GetNodeOrNull("CloseButton") == null);
        view2?.Hide();
        host2.QueueFree();
        Check("配置项测试后没有残留打开中的仓库", WarehouseRegistry.OpenCount == 0);
    }

    // ---------- 3. 主关卡端到端 ----------

    private void TestShopFlow()
    {
        SaveManager.NewGame(_slot);          // 用临时槽，避免污染正式存档
        SaveManager.InFreeMove = false;      // 从 Ready（坐在钓鱼点）起步
        _stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn")
            .Instantiate<Main.MainStage>();
        AddChild(_stage);

        var exitButton = _stage.FindChild("ExitFishingButton", recursive: true, owned: false) as Button;
        var shopUi = _stage.GetNodeOrNull<ShopUi>("UI/Hud/ShopUi");
        var startButton = _stage.GetNodeOrNull<Button>("UI/Hud/ShopUi/StartSellButton");
        Check("鱼店流程前置：退出按钮 / 鱼店 UI / 开始卖鱼按钮 存在",
            exitButton != null && shopUi != null && startButton != null);
        if (exitButton == null || shopUi == null || startButton == null) return;

        Check("开局：Ready + 「开始卖鱼」隐藏", _stage.Phase == Main.MainStage.GamePhase.Ready && !startButton.Visible);

        exitButton.EmitSignal(BaseButton.SignalName.Pressed);
        Check("点「退出钓鱼」→ 准备开店，按钮出现",
            _stage.Phase == Main.MainStage.GamePhase.PrepareShop && startButton.Visible);

        // 找鱼店唯一的仓库（名字 = 仓库），点鱼店把它和价格板一起打开
        var sartineDef = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/Sartine.tres");
        Check("沙丁鱼配置可加载（Id=1 / 物品定义可入库）", sartineDef != null && sartineDef.ItemDef != null);
        if (sartineDef?.ItemDef == null) return;
        var warehouse = WarehouseButton.All.FirstOrDefault(b => b.StorageDef?.DisplayName == "仓库");
        Check("场景里有唯一的「仓库」", warehouse != null);
        if (warehouse == null) return;

        var shop = _stage.GetNodeOrNull<InteractZone>("FishShop");
        Check("场景里有鱼店（点击开关仓库+价格板）", shop != null);
        if (shop != null)
        {
            ClickWorld(shop.GlobalPosition);
            Check("点鱼店 → 仓库打开", warehouse.IsOpen);
            Check("点鱼店 → 价格板一起打开", shopUi.PriceBoardOpen);
            ClickWorld(shop.GlobalPosition);
            Check("再点鱼店 → 仓库关闭", !warehouse.IsOpen);
            Check("再点鱼店 → 价格板一起关闭", !shopUi.PriceBoardOpen);
        }

        // 打开仓库放一条沙丁鱼进去（物品 → 鱼 注册表要认得）
        ClickWorld(shop?.GlobalPosition ?? Vector2.Zero);
        var view = warehouse.View;
        Check("仓库打开后有视图", view != null);
        if (view == null) return;
        var fishItem = sartineDef.ItemDef.ToItem();
        Check("把沙丁鱼放进仓库", view.Inventory.TryPlace(fishItem, Vector2I.Zero).Outcome == PlaceOutcome.Success);

        // 未标价 → 点「开始卖鱼」弹只能关闭的警告，阶段不变
        startButton.EmitSignal(BaseButton.SignalName.Pressed);
        var warnClose = _stage.GetNodeOrNull<Button>("UI/Hud/ShopUi/WarnOverlay/Center/Panel/VBox/CloseButton");
        Check("未标价：弹出警告、阶段仍是准备开店",
            shopUi.ModalOpen && _stage.Phase == Main.MainStage.GamePhase.PrepareShop);
        Check("未标价：警告只有一个关闭按钮", warnClose != null);
        warnClose?.EmitSignal(BaseButton.SignalName.Pressed);

        // 关掉再打开鱼店 → 价格面板按当前仓库里的鱼重建（1 行）
        ClickWorld(shop?.GlobalPosition ?? Vector2.Zero);   // 关
        ClickWorld(shop?.GlobalPosition ?? Vector2.Zero);   // 开
        var rows = _stage.GetNodeOrNull<VBoxContainer>("UI/Hud/ShopUi/PricePanel/VBox/Scroll/Rows");
        Check("价格面板：仓库里只有 1 种鱼 → 1 行", shopUi.PriceBoardOpen && rows != null && rows.GetChildCount() == 1);

        // 填价（模拟输入框只收数字：直接写定价，等价于玩家输入），再关掉鱼店
        SaveManager.ShopPrices[sartineDef.Id] = 40;
        ClickWorld(shop?.GlobalPosition ?? Vector2.Zero);
        Check("再点鱼店：仓库与价格板都关闭", !warehouse.IsOpen && !shopUi.PriceBoardOpen);

        // 鱼护里还有鱼 → 点「开始卖鱼」先弹是/否确认；点「否」不进入卖鱼
        var keep = WarehouseButton.All.FirstOrDefault(b => b.StorageDef?.DisplayName == "鱼护");
        Check("场景里的鱼护（原宝箱）叫「鱼护」", keep != null);
        if (keep != null)
        {
            keep.Open();
            var keepView = keep.View;
            var strayFish = sartineDef.ItemDef.ToItem();
            Check("往鱼护里放一条鱼", keepView != null
                && keepView.Inventory.TryPlace(strayFish, Vector2I.Zero).Outcome == PlaceOutcome.Success);

            startButton.EmitSignal(BaseButton.SignalName.Pressed);
            var noButton = _stage.GetNodeOrNull<Button>("UI/Hud/ShopUi/ConfirmOverlay/Center/Panel/VBox/Buttons/NoButton");
            Check("鱼护有鱼：弹出是/否确认、阶段不变",
                shopUi.ModalOpen && _stage.Phase == Main.MainStage.GamePhase.PrepareShop && noButton != null);
            noButton?.EmitSignal(BaseButton.SignalName.Pressed);
            Check("确认弹窗点「否」：不进入卖鱼", _stage.Phase == Main.MainStage.GamePhase.PrepareShop);

            // 把这条鱼拿掉（等价玩家把它放进仓库），再卖
            if (keepView != null) keepView.Inventory.RemoveItem(strayFish);
            keep.Close();
        }

        // 鱼护（FishKeep）里没有鱼 → 直接开始卖鱼
        startButton.EmitSignal(BaseButton.SignalName.Pressed);
        Check("鱼护没鱼：直接进入卖鱼", _stage.Phase == Main.MainStage.GamePhase.Selling);
        Check("结算结果已生成", _stage.LastSellResult != null);
        var result = _stage.LastSellResult;
        Check("结算：金币到账 = 总收入", SaveManager.Money == (result?.TotalIncome ?? -1));
        Check("结算：卖掉的鱼从仓库移除",
            view.Inventory.PlacedItems.Count() == (result?.RemainingCount.GetValueOrDefault(sartineDef.Id) ?? -1));
        Check("结算面板打开", shopUi.ResultOpen);
        Check("卖鱼种子已记录（确定性防刷）", SaveManager.LastSellSeed == (result?.Seed ?? -2));

        // 图鉴价格：卖出去过 → 记录最高成交价；没卖出去 → 无价
        if (result != null && result.SoldCount.ContainsKey(sartineDef.Id))
        {
            Check("图鉴记录成交价",
                FishEncyclopediaLog.TryGetPrice(sartineDef.Id, out var best)
                && best == result.BestPrice[sartineDef.Id]
                && best == SaveManager.ShopPrices[sartineDef.Id]);
        }
        else
        {
            Check("没有卖出去 → 图鉴仍无价格", !FishEncyclopediaLog.TryGetPrice(sartineDef.Id, out _));
        }

        // 结算面板「结束」→ 自由移动
        _stage.GetNodeOrNull<Button>("UI/Hud/ShopUi/ResultPanel/VBox/CloseButton")
            ?.EmitSignal(BaseButton.SignalName.Pressed);
        Check("点「结束」→ 进入自由移动", _stage.Phase == Main.MainStage.GamePhase.FreeMove);
        Check("自由移动：「开始卖鱼」按钮隐藏", !startButton.Visible);
        Check("自由移动阶段随存档保存", SaveManager.Phase == SavePhase.FreeMove);

        // 自由移动阶段：走回钓鱼点坐下 → Ready，鱼护自动搬回钓鱼区
        var player = _stage.GetNodeOrNull<CharacterBody2D>("Player");
        var spot = _stage.GetNodeOrNull<FishingSpot>("FishingSpot");
        var keepSprite = _stage.GetNodeOrNull<Sprite2D>("FishKeep");
        Check("自由移动前置：Player / FishingSpot / 鱼护 存在",
            player != null && spot != null && keepSprite != null);
        if (player == null || spot == null) return;
        var keepAtShopPos = keepSprite?.GlobalPosition ?? Vector2.Zero;
        var highlight = spot.GetNodeOrNull<Line2D>("Highlight");

        // 范围外点椅子无效
        player.GlobalPosition = new Vector2(spot.GlobalPosition.X + 400f, player.GlobalPosition.Y);
        ClickWorld(spot.GlobalPosition);
        Check("自由移动·范围外点椅子：不触发", _stage.Phase == Main.MainStage.GamePhase.FreeMove);
        _stage.UpdateFishSpotUi(spot.GlobalPosition);
        Check("自由移动·范围外：高亮关闭", highlight != null && !highlight.Visible);

        // 范围内：高亮 + 点椅子 → 走向 → Ready
        player.GlobalPosition = new Vector2(spot.GlobalPosition.X + 40f, player.GlobalPosition.Y);
        _stage.UpdateFishSpotUi(spot.GlobalPosition);
        Check("自由移动·范围内：高亮显示", highlight is { Visible: true });
        ClickWorld(spot.GlobalPosition);
        for (var i = 0; i < 3000; i++)
        {
            _stage.StepPlayerMovement(1.0 / 60.0);
            if (_stage.Phase == Main.MainStage.GamePhase.Ready) break;
        }
        Check("自由移动点椅子：走向钓鱼点并进入 Ready", _stage.Phase == Main.MainStage.GamePhase.Ready);
        Check("回到 Ready：鱼护搬回钓鱼区（离开鱼店位置）",
            keepSprite != null && keepSprite.GlobalPosition.DistanceTo(keepAtShopPos) > 10f);
    }

    // ---------- 工具 ----------

    private static FishDef MakeFish(long id, params FishTagDef[] tags) => new()
    {
        Id = id,
        DisplayName = $"测试鱼{id}",
        Tags = tags,
        ItemDef = new PlacementItemTypeDef { DisplayName = $"测试鱼{id}", SellPrice = 100 },
    };

    private static SellService.Lot MakeLot(FishDef fish, int price, int value) => new()
    {
        Fish = fish,
        Price = price,
        Value = value,
        Item = new InventoryItem(PlacementShape.Rectangle(1, 1)),
        View = null,
    };

    private static CustomerDef MakeCustomer(string name, int minBudget, int maxBudget, params FishTagDef[] tags) => new()
    {
        DisplayName = name,
        PreferenceTags = tags,
        MinBudget = minBudget,
        MaxBudget = maxBudget,
    };

    /// <summary>合成一次落在世界坐标的左键点击（派给 MainStage._UnhandledInput，与真实输入同路）。</summary>
    private void ClickWorld(Vector2 worldPos)
    {
        var screen = _stage.GetViewport().GetCanvasTransform() * worldPos;
        var ev = new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = screen,
            GlobalPosition = screen,
        };
        _stage._UnhandledInput(ev);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SaleFish.Common;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.Localization;
using SaleFish.Save;
using SaleFish.Shop;
using SaleFish.View;

namespace SaleFish.Main;

/// <summary>
/// MainStage 的「鱼店 / 卖鱼」部分（partial，功能独立成文件，场景仍是一个主关卡）：
///
/// 流程：
///  Ready --点「退出钓鱼」--> <b>PrepareShop 准备开店</b>（玩家 + 鱼护 传送到鱼店）
///    · 可把鱼从鱼护拖进 仓库1~5；点价格板逐种定价（0~999）
///    · 右下角「开始卖鱼」：有鱼未标价 → 只能关闭的警告；鱼护还有鱼 → 是/否确认
///  --> <b>Selling 开始卖鱼</b>（快速结算：SellService 纯逻辑）
///    · 结算结果立即落地（金币到账、卖掉的鱼从仓库移除、图鉴记录最高成交价）并自动存档
///    · 弹出结算面板（总收入 / 每种鱼卖出与剩余 / 详细信息 = 每个顾客买了什么）
///    · 点「结束」→ FreeMove 自由移动；走回钓鱼点坐下 → Ready 时鱼护自动搬回钓鱼区
/// </summary>
public sealed partial class MainStage
{
    // ---- 鱼店的场景引用（位置都在场景里，直接拖节点调；这里只接逻辑） ----

    /// <summary>鱼店（InteractZone：Sprite + Detect + Highlight）：玩家靠近显示高亮。</summary>
    [Export] public NodePath? FishShopPath = new("FishShop");

    /// <summary>鱼护（原宝箱，挂 WarehouseButton）：准备开店时搬到鱼店旁，回到 Ready 时搬回原位。</summary>
    [Export] public NodePath? FishKeepPath = new("FishKeep");

    /// <summary>玩家传送到鱼店时的落点（Marker2D，位置可拖）。</summary>
    [Export] public NodePath? ShopSpawnPath = new("ShopSpawn");

    /// <summary>鱼护在鱼店旁的位置（Marker2D，位置可拖）。</summary>
    [Export] public NodePath? FishKeepAtShopPath = new("FishKeepAtShop");

    /// <summary>鱼店唯一的仓库（挂 WarehouseButton 的节点，本身不显示/不可点——点击开关走鱼店）。
    /// 打开它 = 同时打开价格板；关闭 = 同时关闭（见 <see cref="ToggleShopPanels"/>）。</summary>
    [Export] public NodePath? ShopWarehousePath = new("ShopWarehouse");

    /// <summary>鱼店 UI（prefab：Scenes/ShopUi.tscn，挂在 UI/Hud 下）。</summary>
    [Export] public NodePath? ShopUiPath = new("UI/Hud/ShopUi");

    /// <summary>一场卖鱼最少来几位顾客。</summary>
    [Export(PropertyHint.Range, "0,20,1")] public int MinCustomerCount = 1;

    /// <summary>一场卖鱼最多来几位顾客。</summary>
    [Export(PropertyHint.Range, "0,20,1")] public int MaxCustomerCount = 5;

    private InteractZone? _fishShop;
    private Sprite2D? _fishKeepSprite;
    private Node2D? _shopSpawn;
    private Node2D? _fishKeepAtShop;
    private WarehouseButton? _shopWarehouse;
    private ShopUi? _shopUi;
    private Vector2 _fishKeepHome;          // 鱼护在钓鱼区的原位（场景初始位置）
    private SellService.Result? _lastSell;  // 最近一次结算结果（详情/测试用）

    /// <summary>最近一次卖鱼结算结果（null = 还没卖过；公开只读，测试断言用）。</summary>
    public SellService.Result? LastSellResult => _lastSell;

    /// <summary>_Ready 里调用：解析鱼店相关节点、接 UI 事件、记住鱼护原位。</summary>
    private void InitShop()
    {
        _fishShop = GetNodeOrNull<InteractZone>(FishShopPath);
        _fishKeepSprite = GetNodeOrNull<Sprite2D>(FishKeepPath);
        _shopSpawn = GetNodeOrNull<Node2D>(ShopSpawnPath);
        _fishKeepAtShop = GetNodeOrNull<Node2D>(FishKeepAtShopPath);
        _shopWarehouse = GetNodeOrNull<WarehouseButton>(ShopWarehousePath);
        _shopUi = GetNodeOrNull<ShopUi>(ShopUiPath);

        if (_fishKeepSprite != null) _fishKeepHome = _fishKeepSprite.GlobalPosition;
        if (_shopUi != null)
        {
            _shopUi.StartSellPressed += OnStartSellPressed;
            _shopUi.ConfirmAccepted += OnSellConfirmed;
            _shopUi.ResultClosed += OnSellResultClosed;
            _shopUi.PriceBoardClosed += OnPriceBoardClosedByUser;
        }
        if (_fishShop == null) GD.PushWarning("[MainStage] 找不到 FishShop（鱼店）：靠近高亮不可用。");
        if (_fishKeepSprite == null) GD.PushWarning("[MainStage] 找不到 FishKeep（鱼护）：开店传送不可用。");
    }

    /// <summary>「退出钓鱼」= 去鱼店：把玩家和鱼护一起传送到鱼店，进入准备开店阶段。</summary>
    public void GoToShop()
    {
        if (Phase != GamePhase.Ready) return;
        if (_player != null && _shopSpawn != null) _player.GlobalPosition = _shopSpawn.GlobalPosition;
        if (_player != null && _camera != null)
            _camera.Position = new Vector2(_player.GlobalPosition.X, _camera.Position.Y);
        PlaceFishKeepAtShop();
        _sitFishTooltip?.Hide();
        SetPhase(GamePhase.PrepareShop);
        _status.Text = Localizer.T("shop.prepare.enter");
        RefreshButtons();
        RefreshShopHud();
    }

    /// <summary>把鱼护搬到鱼店旁（准备开店时）。</summary>
    private void PlaceFishKeepAtShop()
    {
        if (_fishKeepSprite != null && _fishKeepAtShop != null && IsInstanceValid(_fishKeepSprite))
            _fishKeepSprite.GlobalPosition = _fishKeepAtShop.GlobalPosition;
    }

    /// <summary>把鱼护搬回钓鱼区原位（回到 Ready 时）。</summary>
    private void ReturnFishKeepHome()
    {
        if (_fishKeepSprite != null && IsInstanceValid(_fishKeepSprite))
            _fishKeepSprite.GlobalPosition = _fishKeepHome;
    }

    /// <summary>每帧：鱼店高亮 + 「开始卖鱼」按钮显隐（仅准备开店、且没有弹窗时）。</summary>
    public void UpdateShopUi()
    {
        if (_fishShop != null && _player != null)
            _fishShop.SetHighlight(_fishShop.IsPointInside(_player.GlobalPosition));
        RefreshShopHud();
    }

    /// <summary>刷新「开始卖鱼」按钮显隐（准备开店且无弹窗时才显示）。</summary>
    private void RefreshShopHud()
    {
        _shopUi?.SetStartSellVisible(Phase == GamePhase.PrepareShop && !_shopUi.ModalOpen && !_shopUi.ResultOpen);
    }

    // ---- 鱼店开关（点鱼店 = 仓库 + 价格板 一起开/关） ----

    /// <summary>点击鱼店：仓库与价格板一起打开；已经打开着就一起关闭。只在准备开店阶段有效。</summary>
    public void ToggleShopPanels()
    {
        if (Phase != GamePhase.PrepareShop) return;
        if (_shopWarehouse == null || !IsInstanceValid(_shopWarehouse))
        {
            GD.PushWarning("[MainStage] 鱼店还没接 ShopWarehouse（挂 WarehouseButton 的仓库节点）。");
            return;
        }
        if (_shopWarehouse.IsOpen)
        {
            _shopWarehouse.Close();
            _shopUi?.ClosePriceBoard();
            return;
        }
        _shopWarehouse.Open();
        _shopUi?.OpenPriceBoard(BuildPriceRows());
    }

    /// <summary>价格面板被玩家关掉（面板上的关闭按钮）→ 仓库也一起关（两者同开同关）。</summary>
    private void OnPriceBoardClosedByUser()
    {
        if (_shopWarehouse is { IsOpen: true } && IsInstanceValid(_shopWarehouse)) _shopWarehouse.Close();
        RefreshShopHud();
    }

    /// <summary>仓库内容变化时（拿起/放下/钓上等）如果价格板开着，就重建行（已填价格存在 SaveManager 里不会丢）。</summary>
    private void RefreshPriceBoardIfOpen() => _shopUi?.RefreshPriceBoard(BuildPriceRows());

    /// <summary>价格面板的行：仓库里出现过的每种鱼一行（含图鉴历史最高成交价）。</summary>
    private List<ShopUi.PriceRow> BuildPriceRows()
    {
        var rows = new List<ShopUi.PriceRow>();
        foreach (var fish in DistinctShopFish())
        {
            rows.Add(new ShopUi.PriceRow(
                fish.Id, fish.LocalizedName,
                FishEncyclopediaLog.TryGetPrice(fish.Id, out var hist) ? hist : null));
        }
        rows.Sort((a, b) => a.FishId.CompareTo(b.FishId));
        return rows;
    }

    /// <summary>仓库（不含鱼护）里出现过的鱼种（按 FishDef.Id 去重）。</summary>
    private IEnumerable<FishDef> DistinctShopFish() =>
        EnumerateShopWarehouseItems().Select(x => x.Fish).DistinctBy(f => f.Id);

    /// <summary>遍历「仓库」里的每一条鱼（不含鱼护；查不到鱼定义的物品跳过）。</summary>
    private IEnumerable<(InventoryItem Item, FishDef Fish, InventoryView View)> EnumerateShopWarehouseItems()
    {
        var keepPath = (_fishKeepSprite as WarehouseButton)?.StorageDef?.ResourcePath;
        foreach (var btn in WarehouseButton.All)
        {
            var view = btn.View;
            if (view == null || !GodotObject.IsInstanceValid(view)) continue;
            var path = btn.StorageDef?.ResourcePath;
            if (!string.IsNullOrEmpty(keepPath) && path == keepPath) continue;   // 鱼护不是卖货仓库
            foreach (var item in view.Inventory.PlacedItems)
                if (FishItemRegistry.TryGetFish(item, out var fish))
                    yield return (item, fish, view);
        }
    }

    // ---- 开始卖鱼 ----

    /// <summary>「开始卖鱼」：先校验标价（有未标价 → 只能关闭的警告），再看鱼护是否还有鱼（是 → 是/否确认）。</summary>
    private void OnStartSellPressed()
    {
        if (Phase != GamePhase.PrepareShop || _shopUi == null) return;
        if (!AllShopFishPriced())
        {
            _shopUi.ShowWarning(Localizer.T("shop.warn.unpriced"));
            return;
        }
        if (FishKeepHasFish())
        {
            _shopUi.AskConfirm(Localizer.T("shop.confirm.keepnet"));
            return;
        }
        BeginSelling();
    }

    /// <summary>确认弹窗点「是」→ 开始卖鱼。</summary>
    private void OnSellConfirmed()
    {
        if (Phase != GamePhase.PrepareShop) return;
        BeginSelling();
    }

    /// <summary>仓库里每种鱼都必须填过价格（0 也算填过；没这条记录 = 未标价）。</summary>
    private bool AllShopFishPriced() =>
        DistinctShopFish().All(f => SaveManager.ShopPrices.ContainsKey(f.Id));

    /// <summary>鱼护里还有鱼吗（鱼护 = 那个始终存在的宝箱仓库；从没打开过 = 空）。</summary>
    private bool FishKeepHasFish()
    {
        if (_fishKeepSprite is not WarehouseButton keep) return false;
        var view = keep.View;
        return view != null && GodotObject.IsInstanceValid(view) && view.Inventory.PlacedItems.Any();
    }

    /// <summary>开始卖鱼（快速结算）：确定性种子 → 结算 → 立即落地（金币/移除/图鉴/自动存档）→ 显示结算面板。</summary>
    private void BeginSelling()
    {
        var lots = BuildLots();
        var customers = CustomerRegistry.All;
        var seed = SellService.DeriveSeed(lots);
        SaveManager.LastSellSeed = seed;

        SetPhase(GamePhase.Selling);   // 过程态（不落盘）
        _shopUi?.ClosePriceBoard();
        var result = SellService.Run(lots, customers, MinCustomerCount, MaxCustomerCount, seed);
        ApplySellResult(result);
        _lastSell = result;

        _shopUi?.ShowResult(BuildSellSummary(result), BuildSellDetails(result), result.Customers.Count > 0);
        RefreshShopHud();
    }

    /// <summary>把货架上的鱼打包成结算输入（价格取价格板填的标价，价值取物品定义上的 SellPrice）。</summary>
    private List<SellService.Lot> BuildLots()
    {
        var lots = new List<SellService.Lot>();
        foreach (var (item, fish, view) in EnumerateShopWarehouseItems())
        {
            lots.Add(new SellService.Lot
            {
                Item = item,
                View = view,
                Fish = fish,
                Value = (item.Data as PlacementItemTypeDef)?.SellPrice ?? 0,
                Price = SaveManager.ShopPrices.TryGetValue(fish.Id, out var p) ? p : 0,
            });
        }
        return lots;
    }

    /// <summary>结算落地：卖掉的鱼从仓库移除、金币入账、图鉴记录每种鱼的最高成交价，然后自动存档一次。</summary>
    private void ApplySellResult(SellService.Result result)
    {
        var touched = new HashSet<InventoryView>();
        foreach (var lot in result.Sold)
        {
            if (lot.View == null || !GodotObject.IsInstanceValid(lot.View)) continue;
            lot.View.Inventory.RemoveItem(lot.Item);
            if (touched.Add(lot.View)) lot.View.QueueRedraw();
        }
        SaveManager.Money += result.TotalIncome;
        foreach (var (fishId, best) in result.BestPrice)
            FishEncyclopediaLog.RecordSale(fishId, best);
        SaveManager.SaveNow();   // 卖鱼开始时自动存档（结算即时完成，存的就是结果，读档不会重刷）
        _status.Text = Localizer.Tf("shop.sold.done", result.TotalIncome);
    }

    /// <summary>结算面板「结束」→ 收面板 + 进入自由移动（玩家可以走回钓鱼点坐下）。</summary>
    private void OnSellResultClosed()
    {
        _shopUi?.HideResult();
        _lastSell = null;
        SetPhase(GamePhase.FreeMove);
        _status.Text = Localizer.T("status.freemove");
        RefreshShopHud();
    }

    /// <summary>结算摘要：总收入 + 每种鱼卖出条数与成交额 + 剩余条数。</summary>
    private static string BuildSellSummary(SellService.Result result)
    {
        var lines = new List<string> { Localizer.Tf("shop.result.summary", result.TotalIncome) };
        foreach (var (fishId, count) in result.SoldCount.OrderByDescending(kv => kv.Value))
            lines.Add(Localizer.Tf("shop.result.sold", FishNameOf(result, fishId), count, result.SoldRevenue[fishId]));
        foreach (var (fishId, remain) in result.RemainingCount.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key))
            lines.Add(Localizer.Tf("shop.result.remain", FishNameOf(result, fishId), remain));
        if (result.SoldCount.Count == 0) lines.Add(Localizer.T("shop.result.nocustomer"));
        return string.Join("\n", lines);
    }

    /// <summary>从结算数据里找回鱼名（顾客买过的鱼 → 定义在 lot 里）。</summary>
    private static string FishNameOf(SellService.Result result, long fishId) =>
        result.Sold.FirstOrDefault(l => l.Fish.Id == fishId)?.Fish.LocalizedName
        ?? FishItemRegistry.All.FirstOrDefault(f => f.Id == fishId)?.LocalizedName
        ?? fishId.ToString();

    /// <summary>详细信息：每个顾客的预算/花费 + 买了哪些鱼。</summary>
    private static string BuildSellDetails(SellService.Result result)
    {
        var lines = new List<string>();
        foreach (var c in result.Customers)
        {
            if (c.Bought.Count == 0)
            {
                lines.Add(Localizer.Tf("shop.result.customer_empty", c.Def.DisplayName, c.Budget));
                continue;
            }
            lines.Add(Localizer.Tf("shop.result.customer", c.Def.DisplayName, c.Budget, c.Spent));
            foreach (var lot in c.Bought)
                lines.Add(Localizer.Tf("shop.result.customer_item", lot.Fish.LocalizedName, lot.Price));
        }
        return string.Join("\n", lines);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.Localization;
using SaleFish.View;

namespace SaleFish.Save;

/// <summary>
/// 存档编排（autoload，API 全静态）：把运行态快照成 SaveFile、或把 SaveFile 还原回运行态。
/// **纯手动触发**：本类不写任何「自动存档」——需要存的地方（关仓/钓上/退出…）由宿主调
/// <see cref="SaveNow"/> 一行即可，读档/新游戏同理。
///
/// 快照（SaveNow）：语言 / 金币(Money) / 累计钓获(TotalCaught) / 图鉴日志(FishEncyclopediaLog) /
/// 各仓库（经 WarehouseButton.All：物品锚点+旋转+面板位置）/ 手持物品（HeldItemController）。
/// 还原（LoadSlot/NewGame）按：语言 → 金币/钓获 → 图鉴 → 清空+重建仓库 → 手持。
/// 多槽 = SaveService 的文件名；LastSlot 记上次槽，供将来主界面「继续游戏」用。
/// </summary>
public sealed partial class SaveManager : Node
{
    /// <summary>当前操作的槽名（SaveNow/LoadSlot 后写它）；空 = 尚未指定。</summary>
    public static string ActiveSlot { get; private set; } = "";

    /// <summary>存档里的玩家阶段（GamePhase 的落盘投影）：默认 FreeMove —— 所以新游戏 / 直接运行主关卡
    /// 进场都是自由移动；存过档则按存档恢复（Ready / FreeMove / PrepareShop）。
    /// MainStage._Ready 读它决定初始阶段，MainStage.SetPhase 每次切阶段都写回它，SaveNow 落盘。</summary>
    public static SavePhase Phase = SavePhase.FreeMove;

    /// <summary>「是否在自由移动」的便捷读写（兼容旧 API / 测试）：读 = Phase == FreeMove；
    /// 写 true → FreeMove，false → Ready。</summary>
    public static bool InFreeMove
    {
        get => Phase == SavePhase.FreeMove;
        set => Phase = value ? SavePhase.FreeMove : SavePhase.ReadyFishing;
    }

    /// <summary>卖鱼定价（FishDef.Id → 标价）：价格板填写，随存档保存。</summary>
    public static readonly Dictionary<long, int> ShopPrices = [];

    /// <summary>上一场卖鱼的确定性种子（hash 派生）。-1 = 还没卖过。</summary>
    public static long LastSellSeed = -1;

    /// <summary>玩家当前位置（世界坐标）：MainStage 每帧回写，SaveNow 时写进存档。
    /// 没进过主关卡 = null（存档不记位置，读档用场景默认出生点）。</summary>
    public static Vector2? PlayerPosition;

    /// <summary>读档暂存的玩家位置：主菜单里读档时场上还没有玩家，等主关卡 _Ready 调
    /// <see cref="ConsumePendingPlayerPosition"/> 摆放。取走即清空。</summary>
    private static Vector2? _pendingPlayerPosition;

    /// <summary>读档暂存的仓库内容：主菜单里读档时场上还没有仓库按钮，要等主关卡 _Ready（按钮就位后）
    /// 经 <see cref="ApplyPendingWarehouses"/> 铺回场景；还原完即清空。</summary>
    private static List<WarehouseSave>? _pendingWarehouses;

    /// <summary>金币（本期只建变量与读写口，卖鱼流程后续接）。</summary>
    public static long Money;

    /// <summary>累计钓上条数（图鉴计数用）。</summary>
    public static long TotalCaught;

    private const string LastSlotFile = "user://saves/lastslot.txt";

    /// <summary>默认槽名（SaveNow 未指定槽时兜底）。</summary>
    public const string DefaultSlot = "1";

    /// <summary>钓上某鱼：图鉴置位 + 累计加一（主关卡 OnHookCaught 里接一行即可）。</summary>
    public static void NotifyCaught(FishDef? def)
    {
        if (def == null) return;
        FishEncyclopediaLog.Caught(def.Id);
        TotalCaught++;
    }

    /// <summary>
    /// 保存当前运行态到常规槽（未指定 = 当前 ActiveSlot，再无则 DefaultSlot）。
    /// 存档的完整内容由 BuildSnapshot 组装：语言/金币/钓获/图鉴/仓库/手持。
    /// </summary>
    public static void SaveNow(string? slot = null)
    {
        if (string.IsNullOrEmpty(slot))
            slot = string.IsNullOrEmpty(ActiveSlot) ? DefaultSlot : ActiveSlot;
        SaveService.Save(slot, BuildSnapshot());
        ActiveSlot = slot;
        WriteLastSlot(slot);
        GD.Print($"[Save] 已保存槽「{slot}」");
    }

    /// <summary>读取槽并还原运行态（两段式：先在任意场景还原语言/金币/钓获/图鉴/手持，仓库内容暂存，
    /// 等主关卡 _Ready 后经 <see cref="ApplyPendingWarehouses"/> 铺回——主菜单时场上还没有仓库按钮）。
    /// 槽不存在返回 false（不做任何改变）。</summary>
    public static bool LoadSlot(string slot)
    {
        var data = SaveService.Load(slot);
        if (data == null) return false;
        ApplyCore(data);
        if (data.Held != null) ApplyHeld(data.Held);   // 手是 autoload，跨场景存活，现在就拿上
        _pendingWarehouses = data.Warehouses;
        ActiveSlot = slot;
        WriteLastSlot(slot);
        GD.Print($"[Save] 已读取槽「{slot}」");
        return true;
    }

    /// <summary>开新档：清空所有运行态（金币/钓获/图鉴/仓库暂存/手持）后立即保存到该槽。</summary>
    public static void NewGame(string slot)
    {
        Money = 0;
        TotalCaught = 0;
        FishEncyclopediaLog.ClearAll();
        _pendingWarehouses = null;   // 全新仓库：主关卡按空仓起步
        HeldItemController.Instance?.DiscardHeld();
        Phase = SavePhase.FreeMove;   // 新游戏：进场让玩家处于自由移动，并随存档保存
        ShopPrices.Clear();           // 新游戏：定价清空
        LastSellSeed = -1;
        PlayerPosition = null;       // 新游戏：不记旧位置，从场景默认出生点开始
        _pendingPlayerPosition = null;
        SaveNow(slot);
    }

    /// <summary>主关卡 _Ready 调用：把读档暂存的仓库内容铺到场景（此时场景里仓库按钮已注册）；没有暂存则不动。
    /// 铺完（或没有暂存）都会把所有仓库**关掉** —— 开/关状态不随存档保存，读档一律从「全部关闭」开始。</summary>
    public static void ApplyPendingWarehouses()
    {
        var data = _pendingWarehouses;
        _pendingWarehouses = null;
        if (data != null)
        {
            ClearWarehouses();
            foreach (var ws in data) ApplyWarehouse(ws);
        }
        CloseAllWarehouses();
    }

    /// <summary>测试/工具用：解除当前活动槽（之后的自动存档不再写盘）。</summary>
    public static void DetachActiveSlot() => ActiveSlot = "";

    /// <summary>关闭所有在管仓库（隐藏；内容与记住的位置都保留）。</summary>
    public static void CloseAllWarehouses()
    {
        foreach (var btn in WarehouseButton.All) btn.Close();
    }

    /// <summary>组装当前状态成 SaveFile（不落盘；保存内容 / 测试直接可用）。</summary>
    public static SaveFile BuildSnapshot()
    {
        return new SaveFile
        {
            Version = 4,
            SavedAt = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds(),
            Language = Localizer.CurrentLanguage,
            Money = Money,
            TotalCaught = TotalCaught,
            InFreeMove = Phase == SavePhase.FreeMove,   // 兼容旧字段（v4 起以 Phase 为准）
            Phase = (int)Phase,
            ShopPrices = [.. ShopPrices.Select(kv => new ShopPriceSave { FishId = kv.Key, Price = kv.Value })],
            LastSellSeed = LastSellSeed,
            PlayerX = PlayerPosition?.X,
            PlayerY = PlayerPosition?.Y,
            Encyclopedia = FishEncyclopediaLog.Snapshot(),
            Held = BuildHeldSnapshot(),
            Warehouses = BuildWarehouseSnapshots(),
        };
    }

    /// <summary>把一份存档还原进当前运行态（含场景相关的仓库/手持——仅当场景就绪时调用；测试也可直接驱动）。
    /// 还原后所有仓库一律关闭（开/关状态不保存）。</summary>
    public static void Apply(SaveFile data)
    {
        if (data == null) return;
        ApplyCore(data);
        if (data.Held != null) ApplyHeld(data.Held);
        ClearWarehouses();
        if (data.Warehouses != null)
            foreach (var ws in data.Warehouses)
                ApplyWarehouse(ws);
        CloseAllWarehouses();
    }

    /// <summary>与场景无关的还原（语言/金币/钓获/图鉴）：主菜单读档前即可用。</summary>
    private static void ApplyCore(SaveFile data)
    {
        Localizer.SetLanguage(string.IsNullOrEmpty(data.Language) ? Localizer.DefaultLanguage : data.Language);
        Money = data.Money;
        TotalCaught = data.TotalCaught;
        // v4 起以 Phase 为准；旧档（Phase = -1）回退看 InFreeMove（true = 自由移动，false = 准备钓鱼）。
        Phase = data.Phase >= 0
            ? (SavePhase)data.Phase
            : data.InFreeMove ? SavePhase.FreeMove : SavePhase.ReadyFishing;
        ShopPrices.Clear();
        if (data.ShopPrices != null)
            foreach (var p in data.ShopPrices)
                ShopPrices[p.FishId] = p.Price;
        LastSellSeed = data.LastSellSeed;
        _pendingPlayerPosition = ToPlayerPosition(data);
        FishEncyclopediaLog.Restore(data.Encyclopedia);
    }

    /// <summary>存档里的玩家位置（两端都有才算）；旧档没有 = null（用场景默认出生点）。</summary>
    private static Vector2? ToPlayerPosition(SaveFile data) =>
        data.PlayerX is { } x && data.PlayerY is { } y ? new Vector2(x, y) : null;

    /// <summary>主关卡 _Ready 调用：取出读档暂存的玩家位置（没有则 null）并清空——
    /// 只摆一次，之后无关的场景重载不会把玩家又拽回旧位置。</summary>
    public static Vector2? ConsumePendingPlayerPosition()
    {
        var pending = _pendingPlayerPosition;
        _pendingPlayerPosition = null;
        return pending;
    }

    /// <summary>最近一次存档的槽（主菜单「继续游戏」用）：优先 LastSlot（文件已删则取修改时间最新的槽）；全空返回 null。</summary>
    public static string? MostRecentSlot()
    {
        if (LastSlot is { } last && SaveService.Exists(last)) return last;
        string? best = null;
        ulong bestTime = 0;
        foreach (var s in SaveService.ListSlots())
        {
            var t = FileAccess.GetModifiedTime(SaveService.PathFor(s));   // 该绑定返回 ulong
            if (t > bestTime) { bestTime = t; best = s; }
        }
        return best;
    }

    /// <summary>最后一次操作的槽名（存于 user://saves/lastslot.txt）；从没存过返回 null。主界面「继续游戏」读它。</summary>
    public static string? LastSlot
    {
        get
        {
            if (!FileAccess.FileExists(LastSlotFile)) return null;
            var f = FileAccess.Open(LastSlotFile, FileAccess.ModeFlags.Read);
            if (f == null) return null;
            using (f)
            {
                var s = f.GetAsText().Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
    }

    // ---- 快照 ----

    /// <summary>手持物品快照：Data 必须是 PlacementItemTypeDef 且带 res:// 路径（运行时占位 def 无路径，不存）。</summary>
    private static PlacedItemSave? BuildHeldSnapshot()
    {
        var item = HeldItemController.Instance?.HeldItem;
        if (item == null) return null;
        var def = item.Data as PlacementItemTypeDef;
        if (def == null || string.IsNullOrEmpty(def.ResourcePath)) return null;
        return new PlacedItemSave
        {
            ItemDefPath = def.ResourcePath,
            OriginX = item.Origin.X,
            OriginY = item.Origin.Y,
            RotationTurns = item.RotationTurns,
        };
    }

    /// <summary>各仓库快照：只存「创建过」（View 存在）的仓库——从未创建 = 默认空布局，开新档自然回到默认。
    /// 物品按 def 路径 + 锚点 + 旋转次数存。</summary>
    private static List<WarehouseSave> BuildWarehouseSnapshots()
    {
        var list = new List<WarehouseSave>();
        foreach (var btn in WarehouseButton.All)
        {
            if (btn.StorageDef == null) continue;
            var view = btn.View;
            if (view == null || !GodotObject.IsInstanceValid(view)) continue;
            var ws = new WarehouseSave
            {
                StorageDefPath = btn.StorageDef.ResourcePath ?? "",
                PosX = view.Position.X,
                PosY = view.Position.Y,
            };
            foreach (var item in view.Inventory.PlacedItems)
            {
                if (item.Data is not PlacementItemTypeDef def) continue;
                if (string.IsNullOrEmpty(def.ResourcePath)) continue;   // 运行时占位物品（无 .tres）不存
                ws.Items.Add(new PlacedItemSave
                {
                    ItemDefPath = def.ResourcePath,
                    OriginX = item.Origin.X,
                    OriginY = item.Origin.Y,
                    RotationTurns = item.RotationTurns,
                });
            }
            list.Add(ws);
        }
        return list;
    }

    // ---- 还原 ----

    /// <summary>清空所有在管仓库的格子（LoadSlot/NewGame：先回到默认空仓，再按存档铺）。</summary>
    private static void ClearWarehouses()
    {
        foreach (var btn in WarehouseButton.All)
        {
            var view = btn.View;
            if (view == null || !GodotObject.IsInstanceValid(view)) continue;
            view.Inventory.Clear();
        }
    }

    /// <summary>按存档还原一座仓库：按 StorageDefPath 找场景里对应按钮 → 打开（确保视图）→ 清空 → 逐个 TryPlace → 恢复面板位置。</summary>
    private static void ApplyWarehouse(WarehouseSave ws)
    {
        if (string.IsNullOrEmpty(ws.StorageDefPath)) return;
        var btn = WarehouseButton.All.FirstOrDefault(b =>
            b.StorageDef != null && b.StorageDef.ResourcePath == ws.StorageDefPath);
        if (btn == null)
        {
            GD.PushWarning($"[Save] 读档跳过仓库「{ws.StorageDefPath}」：当前场景没有这一座（配置被删？）。");
            return;
        }
        btn.Open();   // 确保视图存在（场景里的仓库按钮已接好 UI 宿主）
        var view = btn.View;
        if (view == null || !GodotObject.IsInstanceValid(view)) return;
        view.Inventory.Clear();

        foreach (var ps in ws.Items)
        {
            if (string.IsNullOrEmpty(ps.ItemDefPath)) continue;
            var def = ResourceLoader.Load<PlacementItemTypeDef>(ps.ItemDefPath);
            if (def == null)
            {
                GD.PushWarning($"[Save] 读档跳过物品「{ps.ItemDefPath}」：物品配置不存在（已删？）。");
                continue;
            }
            var item = def.ToItem();
            for (var i = 0; i < Mod4(ps.RotationTurns); i++) item.RotateClockwise();
            var result = view.Inventory.TryPlace(item, new Vector2I(ps.OriginX, ps.OriginY));
            if (result.Outcome != PlaceOutcome.Success)
                GD.PushWarning($"[Save] 读档物品「{ps.ItemDefPath}」放置失败（{result.Outcome}）：跳过。");
        }

        btn.RestorePosition(new Vector2(ws.PosX, ws.PosY));
    }

    /// <summary>把手持物品放回鼠标上（读档时手恢复：刚钓上就存的鱼要回到手里）。</summary>
    private static void ApplyHeld(PlacedItemSave ps)
    {
        if (string.IsNullOrEmpty(ps.ItemDefPath)) return;
        var def = ResourceLoader.Load<PlacementItemTypeDef>(ps.ItemDefPath);
        if (def == null) return;
        var item = def.ToItem();
        for (var i = 0; i < Mod4(ps.RotationTurns); i++) item.RotateClockwise();
        var anchorView = WarehouseRegistry.LastView();
        HeldItemController.Instance?.TakeExternal(item, anchorView, anchorView?.CellSize);
    }

    private static int Mod4(int n) => ((n % 4) + 4) % 4;

    private static void WriteLastSlot(string slot)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("user://saves/"));
        var f = FileAccess.Open(LastSlotFile, FileAccess.ModeFlags.Write);
        if (f == null) return;
        using (f)
        {
            f.StoreString(slot);
        }
    }
}
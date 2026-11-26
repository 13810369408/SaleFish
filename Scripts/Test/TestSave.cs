using System;
using System.Linq;
using Godot;
using SaleFish.Save;

namespace SaleFish.Test;

/// <summary>
/// 存档 / 图鉴日志测试（纯逻辑，不依赖场景）：
///  ▪ 深度探索：先 80 后 120 → [80,120]；区间内 100 不扩；30 扩为 [30,120]；只扩不缩；
///  ▪ 图鉴日志：已见未钓 / 钓获置位 / 快照 → 清空 → 恢复后全量还原；
///  ▪ SaveService 往返：SaveFile 落盘 → Load 读回逐字段一致（含手持/仓库/物品），Delete 后槽消失；
///  ▪ SaveManager.BuildSnapshot：空场景组装快照不崩、金币/钓获/语言写进。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestSave.tscn --quit-after 240
/// </summary>
public sealed partial class TestSave : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        Run();
        GD.Print($"[SaveTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[SaveTest] ALL PASSED" : "[SaveTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Run()
    {
        const long fishA = 101;
        const long fishB = 202;
        const long fishC = 303;   // 负数钳位用（独立鱼，不污染上面的探索序列）

        // ---- 1. 深度探索：只扩不缩（用户示例 80 → 120 → 100 → 30）----
        FishEncyclopediaLog.ClearAll();
        Check("未见：无区间", !FishEncyclopediaLog.TryGetDepthRange(fishA, out _, out _));
        FishEncyclopediaLog.See(fishA, 80);
        Check("首次见到 80：区间 80–80", Range(fishA) == "80–80");
        FishEncyclopediaLog.See(fishA, 120);
        Check("再见到 120：扩为 80–120", Range(fishA) == "80–120");
        FishEncyclopediaLog.See(fishA, 100);
        Check("区间内 100：不更新（保持 80–120）", Range(fishA) == "80–120");
        FishEncyclopediaLog.See(fishA, 30);
        Check("30 在区间外：扩为 30–120", Range(fishA) == "30–120");
        FishEncyclopediaLog.See(fishA, 50);
        Check("再度 50：已在区间内不缩（30–120）", Range(fishA) == "30–120");

        // ---- 2. 已见未钓 / 钓获置位 ----
        FishEncyclopediaLog.See(fishB, 200);
        Check("fishB 已见未钓", FishEncyclopediaLog.IsSeen(fishB) && !FishEncyclopediaLog.IsCaught(fishB));
        FishEncyclopediaLog.Caught(fishB);
        Check("钓获后 Caught 置位", FishEncyclopediaLog.IsCaught(fishB));

        // ---- 3. 快照 / 恢复：Restore 全量还原 ----
        var snap = FishEncyclopediaLog.Snapshot();
        Check("快照：两条记录且字段一致",
            snap.Count == 2 && snap.Any(r => r.FishId == fishA && r.DepthMin == 30f && r.DepthMax == 120f));
        FishEncyclopediaLog.ClearAll();
        Check("清空后全部不可见", !FishEncyclopediaLog.IsSeen(fishA) && !FishEncyclopediaLog.IsSeen(fishB));
        FishEncyclopediaLog.Restore(snap);
        Check("恢复：区间与钓获还原",
            FishEncyclopediaLog.IsCaught(fishB)
            && FishEncyclopediaLog.TryGetDepthRange(fishA, out var dMin, out var dMax)
            && dMin == 30f && dMax == 120f);

        // ---- 3b. 负数深度（水面以上）：钳到 0 再从 0 起记 —— 独立鱼，不污染上面的探索序列 ----
        FishEncyclopediaLog.See(fishC, -10f);
        Check("负数深度钳到 0",
            FishEncyclopediaLog.TryGetDepthRange(fishC, out var cMin, out _) && cMin == 0f);

        // ---- 4. SaveService 往返（临时槽，验完删除）----
        var slot = "tst_" + Guid.NewGuid().ToString("N")[..8];
        var file = new SaveFile
        {
            Version = 1,
            Language = "zh",
            Money = 12345,
            TotalCaught = 7,
            Encyclopedia = snap,
            Held = new PlacedItemSave
            {
                ItemDefPath = "res://Data/Inventory/Items/SartineItem.tres",
                OriginX = 1,
                OriginY = 2,
                RotationTurns = 1,
            },
            Warehouses =
            [
                new WarehouseSave
                {
                    StorageDefPath = "res://Data/Inventory/Storage/MainBag.tres",
                    PosX = 100f,
                    PosY = 200f,
                    Items =
                    [
                        new PlacedItemSave
                        {
                            ItemDefPath = "res://Data/Inventory/Items/SartineItem.tres",
                            OriginX = 0,
                            OriginY = 0,
                            RotationTurns = 3,
                        },
                    ],
                },
            ],
        };
        SaveService.Save(slot, file);
        Check("保存后槽已存在", SaveService.Exists(slot));
        Check("槽列表包含该槽", SaveService.ListSlots().Contains(slot));
        var back = SaveService.Load(slot);
        Check("读回：语言/金币/钓获", back != null && back.Language == "zh" && back.Money == 12345 && back.TotalCaught == 7);
        Check("读回：图鉴条目 2 条", back?.Encyclopedia.Count == 2);
        Check("读回：手持物品（路径/旋转）",
            back?.Held != null
            && back.Held.ItemDefPath.EndsWith("SartineItem.tres")
            && back.Held.RotationTurns == 1);
        Check("读回：仓库 + 面板位置 + 物品",
            back?.Warehouses.Count == 1
            && back.Warehouses[0].PosX == 100f && back.Warehouses[0].PosY == 200f
            && back.Warehouses[0].Items.Count == 1
            && back.Warehouses[0].Items[0].RotationTurns == 3);
        SaveService.Delete(slot);
        Check("删除后槽不存在", !SaveService.Exists(slot));

        // ---- 5. SaveManager 快照（空场景组装，不崩；语言/金币/钓获写入）----
        SaveManager.Money = 9;
        SaveManager.TotalCaught = 2;
        SaveManager.PlayerPosition = new Vector2(321f, -7f);
        var built = SaveManager.BuildSnapshot();
        Check("BuildSnapshot：金币/钓获/语言写入", built.Money == 9 && built.TotalCaught == 2 && built.Language.Length > 0);
        Check("BuildSnapshot：持久化目录之外的字段有默认", built.Version == 3 && built.Held == null);
        Check("BuildSnapshot：自由移动状态写入（默认 true = 开局自由移动）", built.InFreeMove);
        Check("BuildSnapshot：玩家位置写入", built.PlayerX == 321f && built.PlayerY == -7f);
        Check("BuildSnapshot：图鉴随日志走（fishA 已见）",
            built.Encyclopedia.Any(r => r.FishId == fishA));

        // ---- 6. 快速调用入口（SaveManager 公共 API）：NewGame / SaveNow / LoadSlot / LastSlot ----
        var slot2 = "tst_" + Guid.NewGuid().ToString("N")[..8];
        SaveManager.NewGame(slot2);
        Check("NewGame：落盘 + 清空经济", SaveService.Exists(slot2) && SaveManager.Money == 0 && SaveManager.TotalCaught == 0);
        Check("NewGame：记录上次槽（供「继续游戏」）", SaveManager.LastSlot == slot2);
        Check("NewGame：图鉴清空", !FishEncyclopediaLog.IsSeen(fishA));
        Check("NewGame：记录为自由移动开局", SaveManager.InFreeMove);
        Check("NewGame：清掉旧玩家位置（从默认出生点开始）", SaveManager.PlayerPosition == null);
        Check("读不存在的槽返回 false（不做任何改变）",
            !SaveManager.LoadSlot("no_such_slot_" + slot2) && SaveManager.Money == 0);

        SaveManager.Money = 77;
        SaveManager.InFreeMove = false;   // 造一个「当时正坐在钓鱼点准备钓鱼」的状态，读档应原样恢复
        SaveManager.PlayerPosition = new Vector2(55f, 66f);
        SaveManager.SaveNow(slot2);
        SaveManager.Money = 0;
        SaveManager.TotalCaught = 0;
        SaveManager.InFreeMove = true;
        SaveManager.PlayerPosition = null;
        FishEncyclopediaLog.See(fishB, 5f);   // 造一条运行态变化，读档应还原
        var loaded = SaveManager.LoadSlot(slot2);
        Check("LoadSlot：成功 + 恢复金币/钓获", loaded && SaveManager.Money == 77 && SaveManager.ActiveSlot == slot2);
        Check("LoadSlot：恢复「准备钓鱼」阶段标记（InFreeMove = false）", !SaveManager.InFreeMove);
        var pendingPos = SaveManager.ConsumePendingPlayerPosition();
        Check("LoadSlot：玩家位置进入暂存区（等主关卡 _Ready 摆放）",
            pendingPos is { } pp && pp.DistanceTo(new Vector2(55f, 66f)) < 0.01f);
        Check("LoadSlot：图鉴还原为存档内容（运行期新增的鱼 B 被存档内容覆盖清掉）",
            !FishEncyclopediaLog.IsSeen(fishB));
        SaveService.Delete(slot2);
        Check("清理临时槽", !SaveService.Exists(slot2));
    }

    /// <summary>区间文本（用于断言展示格式）。</summary>
    private static string Range(long id) =>
        FishEncyclopediaLog.TryGetDepthRange(id, out var min, out var max) ? $"{(int)min}–{(int)max}" : "none";

    private void Check(string name, bool cond) => Check(name, cond, null);

    private void Check(string name, bool cond, string? detail)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
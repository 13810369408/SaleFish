using System.Linq;
using Godot;
using SaleFish.Fishing;
using SaleFish.Inventory;

namespace SaleFish.Test;

/// <summary>
/// 钓鱼局外配置（四种 Resource .tres + 鱼标签）测试：验证默认值、空数组语义、样例 .tres 可加载、
/// 交叉引用（鱼 → 鱼钩/鱼饵/饵鱼标签 + 背包形态）正确解析、以及 Hook 上四个装配引用（钩子/真饵/饵鱼/配重）可挂接。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestFishingDefs.tscn --quit-after 240
/// </summary>
public sealed partial class TestFishingDefs : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        Run();
        GD.Print($"[FishingDefsTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[FishingDefsTest] ALL PASSED" : "[FishingDefsTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    private void Run()
    {
        // ---- 1. 四种定义的默认值 ----
        var hookDef = new FishingHookDef();
        var baitDef = new BaitDef();
        var sinkerDef = new SinkerDef();
        var fishDef = new FishDef();
        var lineDef = new FishingLineDef();

        Check("FishingHookDef 默认重量 = 1（基准钩重，沿用 Hook.Weight）", hookDef.Weight == 1f);
        Check("SinkerDef 默认重量 = 1", sinkerDef.Weight == 1f);
        Check("Id 默认 0（五种一致）", hookDef.Id == 0 && baitDef.Id == 0 && sinkerDef.Id == 0 && fishDef.Id == 0 && lineDef.Id == 0);
        Check("DisplayName 默认空（五种一致）", hookDef.DisplayName == "" && baitDef.DisplayName == "" && sinkerDef.DisplayName == "" && fishDef.DisplayName == "" && lineDef.DisplayName == "");
        Check("Texture 默认空（钩/饵/配重）", hookDef.Texture == null && baitDef.Texture == null && sinkerDef.Texture == null);
        Check("FishDef 默认售价 = 0", fishDef.SellPrice == 0f);
        Check("FishDef 默认无背包形态引用", fishDef.ItemDef == null);
        Check("FishDef 默认三个匹配数组全空（空 = 不能钓）",
            fishDef.Hooks.Length == 0 && fishDef.Baits.Length == 0 && fishDef.LiveBaitTags.Length == 0);
        Check("FishDef 默认自身标签空（Tags 空）", fishDef.Tags.Length == 0);
        Check("FishDef 默认体力/耐力 = 10（上钩满值基准）、拖速 = 120",
            fishDef.MaxStamina == 10f && fishDef.MaxEndurance == 10f && fishDef.PullSpeed == 120f);
        Check("FishDef 默认无动画精灵表（SpriteFrames 空）", fishDef.SpriteFrames == null);
        Check("FishDef 默认三个播放速度 = 1（正常/挣扎/虚弱）",
            fishDef.NormalPlaySpeed == 1f && fishDef.StrugglePlaySpeed == 1f && fishDef.ExhaustedPlaySpeed == 1f);
        Check("FishDef 默认精灵缩放 = 6", fishDef.SpriteScale == 6f);
        Check("FishDef 默认转身时长 = 0.2 秒", fishDef.TurnDuration == 0.2f);
        Check("FishDef 默认鱼嘴偏移 = (-1,-1)（未配置 → 嘴 = 鱼中心）", fishDef.MouthOffset == new Vector2(-1f, -1f));
        Check("FishingLineDef 默认最大拉力 = 100、线长 = 1000", lineDef.MaxTension == 100f && lineDef.Length == 1000f);

        // ---- 2. 空数组语义：留空 = 该路匹配为空（钓不到），不是"不限" ----
        var strict = new FishDef { Id = 7, Hooks = [], Baits = [], LiveBaitTags = [] };
        Check("留空数组长度为 0（Hooks/Baits/LiveBaitTags/Tags 都是 0）",
            strict.Hooks.Length == 0 && strict.Baits.Length == 0 && strict.LiveBaitTags.Length == 0 && strict.Tags.Length == 0);

        // ---- 3. 样例 .tres 可加载（钩/饵/配重） ----
        var basicHook = ResourceLoader.Load<FishingHookDef>("res://Data/Fishing/Hooks/BasicHook.tres");
        Check("basic_hook.tres 可加载为 FishingHookDef", basicHook != null);
        Check("基础鱼钩名 = 基础鱼钩", basicHook != null && basicHook.DisplayName == "基础鱼钩");
        Check("基础鱼钩重量 = 1", basicHook != null && basicHook.Weight == 1f);

        var worm = ResourceLoader.Load<BaitDef>("res://Data/Fishing/Baits/WormBait.tres");
        Check("worm_bait.tres 可加载为 BaitDef", worm != null && worm.DisplayName == "蚯蚓");

        var lead = ResourceLoader.Load<SinkerDef>("res://Data/Fishing/Sinkers/LeadSinker.tres");
        Check("lead_sinker.tres 可加载为 SinkerDef", lead != null);
        Check("铅坠重量 = 2", lead != null && lead.Weight == 2f);

        var line = ResourceLoader.Load<FishingLineDef>("res://Data/Fishing/Lines/BasicLine.tres");
        Check("basic_line.tres 可加载为 FishingLineDef", line != null);
        Check("基础鱼线名 = 基础鱼线", line != null && line.DisplayName == "基础鱼线");
        Check("基础鱼线最大拉力 = 100（代码默认）、线长 > 0（手感调参，不硬编码）",
            line != null && line.MaxTension == 100f && line.Length > 0f);

        // ---- 4. 样例鱼 .tres：售价 / 背包形态 / 交叉引用 ----
        // 注：鱼文件在项目演进中从 carp.tres 改名 Sartine.tres、DisplayName 随之改为「沙丁鱼」，
        // 下面断言与检查名对齐当前数据（数据归属用户，测试跟随）。
        var carp = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/Sartine.tres");
        Check("Sartine.tres（原 carp.tres，已改名）可加载为 FishDef", carp != null && carp.DisplayName == "沙丁鱼");
        Check("沙丁鱼售价 = 50", carp != null && carp.SellPrice == 50f);
        Check("沙丁鱼上钩数值 = 体力8/耐力8/拖速90（沙丁鱼弱而快，按习性下调）",
            carp != null && carp.MaxStamina == 8f && carp.MaxEndurance == 8f && carp.PullSpeed == 90f);
        Check("沙丁鱼背包形态引用 SartineItem.tres（两格长条，ToItem 可入库）",
            carp?.ItemDef != null && carp.ItemDef.DisplayName.Length > 0 && carp.ItemDef.ToItem().Shape.CellCount == 2);
        Check("沙丁鱼 Hooks[0] 引用了 basic_hook", carp is { Hooks.Length: > 0 } && ReferenceEquals(carp.Hooks[0], basicHook));
        Check("沙丁鱼 Baits[0] 引用了 worm", carp is { Baits.Length: > 0 } && ReferenceEquals(carp.Baits[0], worm));
        Check("沙丁鱼 LiveBaitTags 留空（没有鱼饵鱼能钓沙丁鱼）", carp != null && carp.LiveBaitTags.Length == 0);
        Check("沙丁鱼 Tags 引用超小型标签（鱼可当饵鱼被更大鱼咬）",
            carp != null && carp.Tags.Length == 1 && carp.Tags[0].DisplayName == "超小型");

        // ---- 5. 饵鱼匹配走标签：饵鱼自身 Tags 与目标鱼 LiveBaitTags 求交集，命中即咬 ----
        var ultraSmall = ResourceLoader.Load<FishTagDef>("res://Data/Fishing/Tags/UltraSmall.tres");
        Check("ultra_small.tres 可加载为 FishTagDef", ultraSmall != null && ultraSmall.DisplayName == "超小型");
        var minnow = new FishDef { Id = 2, DisplayName = "沙丁鱼", SellPrice = 5f, Tags = new[] { ultraSmall! } };
        var pike = new FishDef { Id = 3, DisplayName = "海鲈鱼", SellPrice = 80f, LiveBaitTags = new[] { ultraSmall! } };
        Check("饵鱼标签可挂到鱼自身 Tags（沙丁鱼 = 超小型）",
            minnow.Tags.Length == 1 && ReferenceEquals(minnow.Tags[0], ultraSmall));
        Check("捕食者 LiveBaitTags 引用标签（海鲈鱼 = 超小型）",
            pike.LiveBaitTags.Length == 1 && ReferenceEquals(pike.LiveBaitTags[0], ultraSmall));
        Check("标签匹配：沙丁鱼（超小型）能钓海鲈鱼（吃超小型）",
            minnow.Tags.Any(t => pike.LiveBaitTags.Contains(t)));
        var noTagBait = new FishDef { Id = 4, DisplayName = "无标签鱼", SellPrice = 5f };
        Check("标签匹配：无标签鱼当饵钓不到海鲈鱼（交集空）",
            !noTagBait.Tags.Any(t => pike.LiveBaitTags.Contains(t)));

        // ---- 6. Hook 装配引用：四个槽 + 鱼线可挂接并正确回读 ----
        var hook = new Hook();
        AddChild(hook);   // 进树持有，退出时由场景树统一清理（避免 Area2D RID 泄漏警告）
        hook.HookDef = basicHook;
        hook.Bait = worm;
        hook.LiveBait = minnow;
        hook.Sinker = lead;
        hook.Line = line;
        Check("Hook.HookDef 挂接回读一致", ReferenceEquals(hook.HookDef, basicHook));
        Check("Hook.Bait 挂接回读一致", ReferenceEquals(hook.Bait, worm));
        Check("Hook.LiveBait 挂接回读一致（活饵可挂鱼）", ReferenceEquals(hook.LiveBait, minnow));
        Check("Hook.Sinker 挂接回读一致", ReferenceEquals(hook.Sinker, lead));
        Check("Hook.Line 挂接回读一致（鱼线可挂钩）", ReferenceEquals(hook.Line, line));
        Check("装配不改动运动参数（Weight 仍是钩子自己的）", hook.Weight == 1f);

        var bare = new Hook();
        AddChild(bare);
        Check("新 Hook 四个槽 + 鱼线默认全空",
            bare.HookDef == null && bare.Bait == null && bare.LiveBait == null && bare.Sinker == null && bare.Line == null);
    }
}
using System.Linq;
using Godot;
using SaleFish.Fishing;
using SaleFish.Localization;
using SaleFish.Main;
using SaleFish.Save;

namespace SaleFish.Test;

/// <summary>
/// 本地化（官方 TranslationServer 路线）测试：
/// 源表 Data/Localization/strings.csv → Godot 导入器生成 strings.{zh,en}.translation，已注册进
/// project.godot [internationalization]，引擎启动自动加载；Localizer 只是薄壳封装。
///  ▪ 基础：默认 zh、官方翻译已加载（zh/en）、T/Tf/Lookup、CSV 引号/逗号单元格经官方导入后读回；
///  ▪ 切语言：SetLanguage → TranslationServer locale 变化 + 本服务 LanguageChanged 广播、幂等、未知码归一化；
///  ▪ 官方回退：当前语言缺 key → 按 fallback(zh) 兜底（运行期注入/移除 Translation 验证）；
///  ▪ LocalizedText 组件跟随语言；
///  ▪ 鱼名：FishDef.LocalizationKey + LocalizedName（走 TranslationServer）；
///  ▪ 接线集成：正式 main_stage 的菜单/图鉴/状态栏随语言切换。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestLocalization.tscn --quit-after 240
/// </summary>
public sealed partial class TestLocalization : Node
{
    private int _passed;
    private int _failed;

    public override async void _Ready()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // 等一帧让 autoload/翻译加载稳定
        Run();
        GD.Print($"[LocalizationTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[LocalizationTest] ALL PASSED" : "[LocalizationTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Run()
    {
        // ---- 1. 基础：默认 zh、官方导入的翻译已由 project.godot 注册进 TranslationServer ----
        Check("默认语言 zh（启动时固定）", Localizer.CurrentLanguage == "zh", $"实际 {Localizer.CurrentLanguage}");
        var locales = TranslationServer.GetLoadedLocales();
        Check("官方翻译已加载（zh 与 en）",
            locales.Contains("zh") && locales.Contains("en"), $"[{string.Join(",", locales)}]");
        Check("T 取当前语言译文（zh）", Localizer.T("menu.button.menu") == "菜单", $"实际 {Localizer.T("menu.button.menu")}");
        Check("Tf 页码模板（zh）", Localizer.Tf("menu.page", 2, 3) == "第 2 / 3 页");
        Check("T 缺 key 原样返回 key", Localizer.T("no.such.key") == "no.such.key");
        Check("Lookup 缺 key 返回 null", Localizer.Lookup("no.such.key") == null);
        Check("CSV→官方导入：zh 帮助整段读回", Localizer.T("ui.help").Contains("绿=可放") && Localizer.T("ui.help").Contains("红=冲突"));

        // ---- 2. 切语言：TranslationServer locale + 本服务广播 ----
        var switched = 0;
        Localizer.LanguageChanged += () => switched++;
        Localizer.SetLanguage("en");
        Check("切语言：TranslationServer 当前 locale = en", TranslationServer.GetLocale() == "en", $"实际 {TranslationServer.GetLocale()}");
        Check("切语言：LanguageChanged 广播", switched == 1, $"实际 {switched}");
        Check("T 取 en 译文", Localizer.T("menu.button.menu") == "Menu", $"实际 {Localizer.T("menu.button.menu")}");
        Check("Tf 页码模板（en）", Localizer.Tf("menu.page", 2, 3) == "Page 2 / 3");
        Check("CSV→官方导入：带逗号单元格读回（en 帮助）",
            Localizer.T("ui.help").Contains("LMB pick/drop, RMB rotate 90°") && Localizer.T("ui.help").Contains("red=conflict"));
        Check("CSV→官方导入：带引号单元格读回（en status.empty）",
            Localizer.T("status.empty") == "Empty · click \"Get Item\" to take one", $"实际 {Localizer.T("status.empty")}");
        Check("CSV→官方导入：引号+逗号单元格读回（en 仓库）",
            Localizer.T("status.warehouse.opened") == "Bag \"{0}\" opened (kept, draggable)", $"实际 {Localizer.T("status.warehouse.opened")}");
        Localizer.SetLanguage("en");
        Check("同值切换幂等（不重复广播）", switched == 1, $"实际 {switched}");
        Localizer.SetLanguage("zz");
        Check("未知语言码归一化到默认 zh", Localizer.CurrentLanguage == "zh", $"实际 {Localizer.CurrentLanguage}");
        Localizer.SetLanguage("en");

        // ---- 3. 官方回退：当前语言缺 key → 按 fallback(zh) 兜底（运行期注入 Translation 验证，验完移除）----
        var zhOnly = new Translation { Locale = "zh" };
        zhOnly.AddMessage("only_zh", "只有中文有");
        TranslationServer.AddTranslation(zhOnly);
        var enOnly = new Translation { Locale = "en" };
        enOnly.AddMessage("only_en", "Only English");
        TranslationServer.AddTranslation(enOnly);
        Check("当前语言命中（en 专属 key）", Localizer.T("only_en") == "Only English");
        Check("当前语言缺 key → 回退 fallback(zh)", Localizer.T("only_zh") == "只有中文有", $"实际 {Localizer.T("only_zh")}");
        TranslationServer.RemoveTranslation(zhOnly);
        TranslationServer.RemoveTranslation(enOnly);

        // ---- 4. LocalizedText 组件（场景里挂好脚本+key 的按钮）：_Ready 填文本、切语言自动更新 ----
        Localizer.SetLanguage("zh");
        var btn = GetNode<Button>("LocalizedButton");
        Check("LocalizedText：挂载即按当前语言填文本", btn != null && btn.Text == "继续游戏", $"实际 {btn?.Text}");
        Localizer.SetLanguage("en");
        Check("LocalizedText：切语言自动更新", btn != null && btn.Text == "Resume", $"实际 {btn?.Text}");

        // ---- 5. 鱼名本地化：FishDef.LocalizationKey / LocalizedName（走 localizer）----
        Localizer.SetLanguage("zh");
        var sartine = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/Sartine.tres");
        Check("鱼名：配了 key 走表（zh）", sartine != null && sartine.LocalizedName == "沙丁鱼", $"实际 {sartine?.LocalizedName}");
        Localizer.SetLanguage("en");
        Check("鱼名：切语言跟随（en）", sartine != null && sartine.LocalizedName == "Sardine", $"实际 {sartine?.LocalizedName}");
        var plain = new FishDef { DisplayName = "璐璐", LocalizationKey = "" };
        Check("鱼名：没配 key 用 DisplayName", plain.LocalizedName == "璐璐");
        var stale = new FishDef { DisplayName = "兜底名", LocalizationKey = "no.such.key" };
        Check("鱼名：key 查不到 → 回 DisplayName", stale.LocalizedName == "兜底名");
        Localizer.SetLanguage("zh");

        // ---- 6. 接线集成：正式主关卡跟着语言走 ----
        // 图鉴解锁预置（早于舞台实例化，MenuUi._Ready 按它过滤/点亮）：
        // 4 条预填鱼全部见过、沙丁鱼钓到过（图鉴名称才显示真名，并随语言切换）。
        FishEncyclopediaLog.ClearAll();
        if (ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/Sartine.tres") is { } s1)
        { FishEncyclopediaLog.See(s1.Id, 60f); FishEncyclopediaLog.Caught(s1.Id); }
        if (ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/SeaBass.tres") is { } s2)
            FishEncyclopediaLog.See(s2.Id, 60f);
        if (ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/YellowBoxfish.tres") is { } s3)
            FishEncyclopediaLog.See(s3.Id, 60f);
        if (ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/MolaMola.tres") is { } s4)
            FishEncyclopediaLog.See(s4.Id, 60f);

        // 开局阶段随存档走：本测试以「准备钓鱼」(Ready) 为前提（状态栏文案等断言不受自由移动文案干扰）。
        SaleFish.Save.SaveManager.InFreeMove = false;
        var stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn").Instantiate<MainStage>();
        AddChild(stage);
        var menuButton = stage.GetNode<Button>("UI/Hud/MenuUi/MenuButton");
        var exitFish = stage.FindChild("ExitFishingButton", recursive: true, owned: false) as Button;
        var nameLabel = stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/LeftPage/NameRow/NameText");
        var pageLabel = stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/PageLabel");
        var status = stage.GetNode<Label>("UI/Hud/Status");
        Check("集成前置：菜单按钮/退出钓鱼按钮存在", menuButton != null && exitFish != null);
        Localizer.SetLanguage("zh");
        Check("集成：菜单按钮 zh", menuButton != null && menuButton.Text == "菜单", $"实际 {menuButton?.Text}");
        Check("集成：退出钓鱼按钮 zh", exitFish != null && exitFish.Text == "退出钓鱼");
        Check("集成：图鉴名称 zh", nameLabel != null && nameLabel.Text == "沙丁鱼");
        Check("集成：页码 zh", pageLabel != null && pageLabel.Text == "第 1 / 2 页");
        Localizer.SetLanguage("en");
        Check("集成：菜单按钮 en", menuButton != null && menuButton.Text == "Menu");
        Check("集成：退出钓鱼按钮 en", exitFish != null && exitFish.Text == "Stop Fishing");
        Check("集成：图鉴名称 en", nameLabel != null && nameLabel.Text == "Sardine");
        Check("集成：页码 en", pageLabel != null && pageLabel.Text == "Page 1 / 2");
        Check("集成：状态栏 en（空手提示）", status != null && status.Text.Contains("Empty"), $"实际 {status?.Text}");
        Localizer.SetLanguage("zh");
        stage.Free();
    }

    private void Check(string name, bool cond) => Check(name, cond, null);

    private void Check(string name, bool cond, string? detail)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
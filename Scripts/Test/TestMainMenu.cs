using Godot;
using SaleFish.Localization;
using SaleFish.Main;
using SaleFish.Save;

namespace SaleFish.Test;

/// <summary>
/// 主菜单（MainMenu.tscn，main_scene）交互测试：实例化主菜单场景，SuppressSceneChange 挡住真实切场景。
/// 逐条验证（运行前会备份并清空 user://saves 下三个槽位 + lastslot，测完还原，不碰玩家真档）：
///  ▪ 按钮存在、文字已本地化；三个槽位全空 → 「继续游戏」「加载游戏」隐藏；
///  ▪ 造一个档后主按钮恢复显示；「继续游戏」= 最近槽（加载后 Money 恢复）；
///  ▪ 加载窗口：3 行、空槽禁用、有档槽有删除按钮；
///  ▪ 加载模式点空槽提示；点有档槽 → load:槽；新游戏模式点已占用槽提示，点空槽 → new:槽 + 落盘；
///  ▪ 删除二次确认：否 不删、是 真删（删后主按钮按空档隐藏）；确认文案含档位名；
///  ▪ 设置窗口：语言下拉（切 en 生效，改回 zh）。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestMainMenu.tscn --quit-after 240
/// </summary>
public sealed partial class TestMainMenu : Node
{
    private int _passed;
    private int _failed;

    private MainMenu _menu = null!;
    private Button _continue = null!;
    private Button _load = null!;
    private Button _new = null!;
    private Button _settings = null!;
    private Control _slotPanel = null!;
    private VBoxContainer _slotRows = null!;
    private Label _hint = null!;
    private Control _confirmPanel = null!;
    private Label _confirmLabel = null!;
    private Control _settingsPanel = null!;
    private OptionButton _language = null!;

    // 存档保护：测前备份，测后还原。
    private string?[] _backupSlots = null!;
    private string? _backupLast;

    public override async void _Ready()
    {
        _backupSlots = BackupSlots();
        _backupLast = ReadFileSafe("user://saves/lastslot.txt");
        ClearSlots();
        SaveManager.Money = 0;
        SaveManager.TotalCaught = 0;
        FishEncyclopediaLog.ClearAll();
        Localizer.SetLanguage("zh");

        _menu = ResourceLoader.Load<PackedScene>("res://Scenes/MainMenu.tscn").Instantiate<MainMenu>();
        _menu.SuppressSceneChange = true;   // 挡住真实 ChangeSceneToFile（headless 不切场景）
        AddChild(_menu);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        try
        {
            ResolveNodes();
            Run();
        }
        finally
        {
            // 无论 Run 是否中途异常，都必须把测试写掉的槽位/存档原样还原（不碰玩家真档）。
            CleanSlots();
            RestoreSlots();
        }

        GD.Print($"[MenuTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[MenuTest] ALL PASSED" : "[MenuTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void ResolveNodes()
    {
        _continue = _menu.GetNode<Button>("Center/Column/ContinueButton");
        _load = _menu.GetNode<Button>("Center/Column/LoadButton");
        _new = _menu.GetNode<Button>("Center/Column/NewButton");
        _settings = _menu.GetNode<Button>("Center/Column/SettingsButton");
        _slotPanel = _menu.GetNode<Control>("SlotPanel");
        _slotRows = _menu.GetNode<VBoxContainer>("SlotPanel/SlotCenter/SlotBox/VBox/SlotRows");
        _hint = _menu.GetNode<Label>("SlotPanel/SlotCenter/SlotBox/VBox/HintLabel");
        _confirmPanel = _menu.GetNode<Control>("ConfirmPanel");
        _confirmLabel = _menu.GetNode<Label>("ConfirmPanel/ConfirmCenter/ConfirmBox/VBox/ConfirmLabel");
        _settingsPanel = _menu.GetNode<Control>("SettingsPanel");
        _language = _menu.GetNode<OptionButton>("SettingsPanel/SettingsCenter/SettingsBox/VBox/SettingsUi/LanguageOption");
    }

    private void Run()
    {
        // ---- 1. 基础 + 本地化 key（需 .translation 已重新导入）----
        Check("新 key 本地化（zh）", Localizer.T("menu.button.new") == "新游戏"
            && Localizer.T("menu.slot.hint") == "需选择空白存档位来开始新游戏。");
        Check("五个按钮存在", _continue != null && _load != null && _new != null && _settings != null);
        Check("按钮文字已本地化", _continue!.Text == "继续游戏" && _new!.Text == "新游戏" && _settings!.Text == "设置");

        // ---- 2. 三个槽位全空：继续/加载隐藏；新游戏/设置/退出可见 ----
        Check("全空：继续游戏/加载游戏隐藏", !_continue!.Visible && !_load!.Visible);
        Check("全空：新游戏/设置仍显示", _new!.Visible && _settings!.Visible);
        Check("全空：槽位窗口与弹窗初始隐藏", !_slotPanel.Visible && !_settingsPanel!.Visible && !_confirmPanel.Visible);

        // ---- 3. 造一个档：主按钮恢复显示；「继续游戏」读最近槽 ----
        SaveManager.NewGame("1");
        SaveManager.Money = 5;
        SaveManager.SaveNow("1");
        _menu.RefreshButtonVisibility();
        var savedMoney = SaveManager.Money;   // 5
        Check("有档：继续/加载恢复显示", _continue!.Visible && _load!.Visible);

        Press(_continue);
        Check("继续游戏：读最近槽 1", _menu.PendingStart == "continue:1");
        Check("继续游戏：金币恢复为档内值 5", SaveManager.Money == 5);
        Check("继续游戏：ActiveSlot = 1", SaveManager.ActiveSlot == "1");

        // ---- 4. 加载窗口：三行、空槽禁用、有档槽带删除按钮 ----
        Press(_load);
        Check("加载窗口打开 + 模式 Load", _slotPanel.Visible && _menu.Mode == MainMenu.SlotMode.Load);
        Check("加载窗口：正好 3 行", _slotRows.GetChildCount() == 3);
        Check("加载窗口：槽 1 有档可点 + 有删除按钮", RowHasDelete(0));
        Check("加载窗口：槽 2/3 空槽禁用且无删除按钮",
            RowChoose(1).Disabled && RowChildren(1) == 1
            && RowChoose(2).Disabled && RowChildren(2) == 1);
        Check("加载窗口：槽 1 概要含档位名", RowChoose(0).Text.Contains("档位 1"));
        Check("加载窗口：空槽显示'空'", RowChoose(1).Text.Contains(Localizer.T("menu.slot.empty")));

        Press(RowChoose(2));   // Load 空槽（本来禁用，防御性再按）
        Check("加载模式点空槽：提示该槽无存档", _hint.Visible && _hint.Text == Localizer.T("menu.slot.emptyhint"));

        Press(RowChoose(0));   // Load 有档槽 → load:1
        Check("加载模式点有档槽 → load:1", _menu.PendingStart == "load:1" && SaveManager.Money == 5);

        // ---- 5. 新游戏窗口：已占用槽提示，空槽开新档 ----
        Press(_new);
        Check("新游戏窗口打开 + 模式 New", _slotPanel.Visible && _menu.Mode == MainMenu.SlotMode.New);
        Check("新游戏模式：空槽可点（不禁用）", !RowChoose(2).Disabled);
        Press(RowChoose(0));   // 槽 1 已占用
        Check("新游戏点已占用槽：提示需选空位", _hint.Visible && _hint.Text == "需选择空白存档位来开始新游戏。");
        Press(RowChoose(2));   // 槽 3 空 → new:3
        Check("新游戏点空槽 → new:3 落盘", _menu.PendingStart == "new:3" && SaveService.Exists("3"));
        Check("新游戏点空槽 → 金币归零", SaveManager.Money == 0);

        // ---- 6. 删除二次确认：否不删、是才删 ----
        Press(_load);   // 重开加载窗口（此时 1、3 有档）
        Press(RowDelete(0));   // 槽 1 删除
        Check("删除确认弹窗打开 + 文案含档位名",
            _confirmPanel.Visible && _confirmLabel.Text.Contains("档位 1") && _confirmLabel.Text.Contains("删除后不可找回"));
        Press(_menu.GetNode<Button>("ConfirmPanel/ConfirmCenter/ConfirmBox/VBox/Buttons/NoButton"));
        Check("点'否'：弹窗关闭且档还在", !_confirmPanel.Visible && SaveService.Exists("1"));
        Press(RowDelete(0));
        Press(_menu.GetNode<Button>("ConfirmPanel/ConfirmCenter/ConfirmBox/VBox/Buttons/YesButton"));
        Check("点'是'：档已删 + 弹窗关闭", !_confirmPanel.Visible && !SaveService.Exists("1"));
        Check("删除后槽行刷新（槽 1 行变空 1 子节点）", RowChildren(0) == 1);

        SaveService.Delete("3");
        _menu.RefreshButtonVisibility();
        Check("删到最后一个档：继续/加载再次隐藏", !_continue!.Visible && !_load!.Visible);

        // ---- 7. 设置窗口：语言下拉 ----
        Press(_settings);
        Check("设置窗口打开", _settingsPanel!.Visible);
        Check("语言下拉 2 项且当前选中文", _language != null && _language!.ItemCount == 2 && _language!.Selected == 0);
        _language!.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        Check("下拉选 English → 切语言 en", Localizer.CurrentLanguage == "en");
        Localizer.SetLanguage("zh");
        Check("语言改回中文 → 下拉选中项同步回 0", _language!.Selected == 0);
    }

    // ---- 存档备份/还原：测试要写 user://saves 下固定槽位，先备份、测完还原，不碰玩家真档 ----

    private static string?[] BackupSlots()
    {
        var list = new string?[3];
        for (var i = 0; i < 3; i++)
        {
            var slot = (i + 1).ToString();
            list[i] = ReadFileSafe(SaveService.PathFor(slot));
        }
        return list;
    }

    private static void ClearSlots()
    {
        for (var i = 1; i <= 3; i++) SaveService.Delete(i.ToString());
        SaveService.Delete("nosuch");
    }

    private static void CleanSlots()
    {
        for (var i = 1; i <= 3; i++) SaveService.Delete(i.ToString());
    }

    private void RestoreSlots()
    {
        for (var i = 0; i < 3; i++)
        {
            var slot = (i + 1).ToString();
            var text = _backupSlots[i];
            if (text == null) { SaveService.Delete(slot); continue; }
            WriteFileSafe(SaveService.PathFor(slot), text);
        }
        WriteFileSafe("user://saves/lastslot.txt", _backupLast ?? "");
        if (_backupLast == null) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath("user://saves/lastslot.txt"));
    }

    private static string? ReadFileSafe(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) return null;
        using (f) return f.GetAsText();
    }

    private static void WriteFileSafe(string path, string text)
    {
        if (text.Length == 0) return;
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("user://saves/"));
        var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        using (f) f.StoreString(text);
    }

    // ---- 槽行子节点 ----

    private HBoxContainer RowBox(int index) => _slotRows.GetChild<HBoxContainer>(index)!;
    private Button RowChoose(int index) => RowBox(index).GetChild<Button>(0)!;
    private Button? RowDelete(int index) => RowChildren(index) > 1 ? RowBox(index).GetChild<Button>(1) : null;
    private int RowChildren(int index) => RowBox(index).GetChildCount();
    private bool RowHasDelete(int index) => RowChildren(index) > 1;

    private void Check(string name, bool cond) => Check(name, cond, null);

    private void Check(string name, bool cond, string? detail)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }

    /// <summary>派发按钮 pressed（headless 不经输入队列）。</summary>
    private static void Press(Button? b)
    {
        if (b == null || !GodotObject.IsInstanceValid(b)) return;
        b.EmitSignal(BaseButton.SignalName.Pressed);
    }
}
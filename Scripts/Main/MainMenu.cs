using System;
using Godot;
using SaleFish.Localization;
using SaleFish.Save;

namespace SaleFish.Main;

/// <summary>
/// 主菜单（main_scene 入口，prefab Scenes/MainMenu.tscn）：进入游戏前的启动界面，五个按钮竖向居中。
///  ▪ 继续游戏 — 自动读最近存档（SaveManager.MostRecentSlot）进主关卡；
///  ▪ 加载游戏 — 弹三槽位窗口，选「有档」槽读档进关；
///  ▪ 新游戏 — 同一槽位窗口，只能选「空」槽开新档；点了有档槽提示"需选择空白存档位来开始新游戏"；
///  ▪ （加载/新游戏窗口里）空槽显示"空"，有档槽右侧有删除按钮 → 二次确认弹窗（是/否，删后不可找回）；
///  ▪ 设置 — 弹设置面板（prefab SettingsUi：语言下拉框）；
///  ▪ 退出游戏 — 退出。
/// 三个槽位全空时「继续游戏」「加载游戏」隐藏。
/// 开始游戏统一走 SaveManager（LoadSlot/NewGame）后 ChangeSceneToFile 进主关卡——
/// 读档的仓库内容由 SaveManager 暂存，主关卡 _Ready（SaveManager.ApplyPendingWarehouses）铺回。
/// 测试钩子 <see cref="SuppressSceneChange"/> = true 时不切场景，改记 <see cref="PendingStart"/> 供 headless 断言。
/// 动态文字（槽行/确认文案）订阅 LanguageChanged 跟随设置切语言刷新。
/// </summary>
public sealed partial class MainMenu : Control
{
    [Export] public NodePath? ContinueButtonPath = new("Center/Column/ContinueButton");
    [Export] public NodePath? LoadButtonPath = new("Center/Column/LoadButton");
    [Export] public NodePath? NewButtonPath = new("Center/Column/NewButton");
    [Export] public NodePath? SettingsButtonPath = new("Center/Column/SettingsButton");
    [Export] public NodePath? QuitButtonPath = new("Center/Column/QuitButton");

    [Export] public NodePath? SlotPanelPath = new("SlotPanel");
    [Export] public NodePath? SlotRowsPath = new("SlotPanel/SlotCenter/SlotBox/VBox/SlotRows");
    [Export] public NodePath? HintLabelPath = new("SlotPanel/SlotCenter/SlotBox/VBox/HintLabel");
    [Export] public NodePath? CloseSlotButtonPath = new("SlotPanel/SlotCenter/SlotBox/VBox/CloseButton");

    [Export] public NodePath? SettingsPanelPath = new("SettingsPanel");
    [Export] public NodePath? CloseSettingsButtonPath = new("SettingsPanel/SettingsCenter/SettingsBox/VBox/CloseButton");

    [Export] public NodePath? ConfirmPanelPath = new("ConfirmPanel");
    [Export] public NodePath? ConfirmLabelPath = new("ConfirmPanel/ConfirmCenter/ConfirmBox/VBox/ConfirmLabel");
    [Export] public NodePath? YesButtonPath = new("ConfirmPanel/ConfirmCenter/ConfirmBox/VBox/Buttons/YesButton");
    [Export] public NodePath? NoButtonPath = new("ConfirmPanel/ConfirmCenter/ConfirmBox/VBox/Buttons/NoButton");

    private Button _continue = null!;
    private Button _load = null!;
    private Button _new = null!;
    private Button _settings = null!;
    private Button _quit = null!;

    private Control _slotPanel = null!;
    private VBoxContainer _slotRows = null!;
    private Label _hint = null!;
    private Button _closeSlot = null!;

    private Control _settingsPanel = null!;
    private Button _closeSettings = null!;

    private Control _confirmPanel = null!;
    private Label _confirmLabel = null!;
    private Button _yes = null!;
    private Button _no = null!;

    /// <summary>固定三个槽位（SaveService 槽名 = 文件名）。</summary>
    private const string Slot1 = "1";
    private const string Slot2 = "2";
    private const string Slot3 = "3";

    /// <summary>槽位窗口当前模式：选槽后是读档（Load）还是开新档（New）。</summary>
    public enum SlotMode { Load, New }
    public SlotMode Mode { get; private set; } = SlotMode.Load;

    /// <summary>确认删除弹窗当前针对的槽名；空 = 无待删槽。</summary>
    public string PendingDeleteSlot { get; private set; } = "";

    /// <summary>测试钩子：置 true 时「开始游戏」不真切场景，改记 <see cref="PendingStart"/>。</summary>
    public bool SuppressSceneChange;

    /// <summary>测试钩子：最后一次「开始游戏」请求（"continue" / "load:1" / "new:2"）；仅在 SuppressSceneChange 时记录。</summary>
    public string? PendingStart;

    public override void _Ready()
    {
        ResolveNodes();
        Wire();
        RefreshButtonVisibility();
        Localizer.LanguageChanged += OnLanguageChanged;   // 设置切语言 → 刷新动态文字（槽行/确认文案）
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(this))
            Localizer.LanguageChanged -= OnLanguageChanged;
    }

    // ---- 连线（代码统一连，prefab 不带 [connection]，测试/改名都好追踪）----

    private void ResolveNodes()
    {
        _continue = GetNode<Button>(ContinueButtonPath);
        _load = GetNode<Button>(LoadButtonPath);
        _new = GetNode<Button>(NewButtonPath);
        _settings = GetNode<Button>(SettingsButtonPath);
        _quit = GetNode<Button>(QuitButtonPath);
        _slotPanel = GetNode<Control>(SlotPanelPath);
        _slotRows = GetNode<VBoxContainer>(SlotRowsPath);
        _hint = GetNode<Label>(HintLabelPath);
        _closeSlot = GetNode<Button>(CloseSlotButtonPath);
        _settingsPanel = GetNode<Control>(SettingsPanelPath);
        _closeSettings = GetNode<Button>(CloseSettingsButtonPath);
        _confirmPanel = GetNode<Control>(ConfirmPanelPath);
        _confirmLabel = GetNode<Label>(ConfirmLabelPath);
        _yes = GetNode<Button>(YesButtonPath);
        _no = GetNode<Button>(NoButtonPath);
    }

    private void Wire()
    {
        _continue.Pressed += OnContinuePressed;
        _load.Pressed += () => OpenSlotWindow(SlotMode.Load);
        _new.Pressed += () => OpenSlotWindow(SlotMode.New);
        _settings.Pressed += () => _settingsPanel.Visible = true;
        _quit.Pressed += () => GetTree().Quit();
        _closeSlot.Pressed += () => _slotPanel.Visible = false;
        _closeSettings.Pressed += () => _settingsPanel.Visible = false;
        _yes.Pressed += ConfirmDelete;
        _no.Pressed += () => _confirmPanel.Visible = false;
    }

    // ---- 按钮显隐：三个槽位全空 → 隐藏「继续游戏」「加载游戏」----

    /// <summary>刷新主按钮显隐（打开菜单时 / 删除存档后调用）。</summary>
    public void RefreshButtonVisibility()
    {
        var any = SaveService.ListSlots().Count > 0;
        _continue.Visible = any;
        _load.Visible = any;
    }

    // ---- 开始游戏 ----

    private void OnContinuePressed()
    {
        var slot = SaveManager.MostRecentSlot();
        if (slot == null) return;                                  // 理论上有档才显示此按钮，防御
        if (!SaveManager.LoadSlot(slot)) return;
        StartIntoStage("continue", slot);
    }

    /// <summary>打开三槽位窗口（加载 / 新游戏共用同一窗口，只是选槽语义不同）。</summary>
    public void OpenSlotWindow(SlotMode mode)
    {
        Mode = mode;
        _hint.Visible = false;
        RebuildSlotRows();
        _slotPanel.Visible = true;
    }

    /// <summary>选槽：Load 时空槽 → 提示；New 时有档槽 → 提示；其余 → 读档/开新档并进主关卡。</summary>
    private void OnSlotClicked(string slot, bool hasSave)
    {
        if (Mode == SlotMode.New)
        {
            if (hasSave) { ShowHint(Localizer.T("menu.slot.hint")); return; }
            SaveManager.NewGame(slot);
            StartIntoStage("new", slot);
            return;
        }
        if (!hasSave) { ShowHint(Localizer.T("menu.slot.emptyhint")); return; }
        if (!SaveManager.LoadSlot(slot)) return;
        StartIntoStage("load", slot);
    }

    /// <summary>进主关卡（测试可 SuppressSceneChange 后改记 PendingStart）。</summary>
    private void StartIntoStage(string kind, string slot)
    {
        if (SuppressSceneChange)
        {
            PendingStart = $"{kind}:{slot}";
            return;
        }
        GetTree().ChangeSceneToFile("res://Scenes/MainStage.tscn");
    }

    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.Visible = true;
    }

    // ---- 槽位窗口：动态建三行 ----

    /// <summary>重建三行槽位（打开窗口 / 删除槽 / 切语言时调用）。</summary>
    public void RebuildSlotRows()
    {
        // QueueFree 延迟到帧末销毁；这里先 RemoveChild（立即脱离子树）再 QueueFree（释放内存），
        // 否则重建的瞬间旧行还在挂载，子节点数短暂翻倍（headless 断言/连续重建会踩到）。
        foreach (Node child in _slotRows.GetChildren())
        {
            _slotRows.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var slot in new[] { Slot1, Slot2, Slot3 })
            _slotRows.AddChild(BuildSlotRow(slot, SaveService.Exists(slot)));
    }

    private Control BuildSlotRow(string slot, bool hasSave)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        row.AddThemeConstantOverride("separation", 12);

        var choose = new Button
        {
            Text = SlotRowText(slot, hasSave),
            Disabled = !hasSave && Mode == SlotMode.Load,          // Load 时空槽点不动；New 时空槽可点（开新档）
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 56),
        };
        choose.AddThemeFontSizeOverride("font_size", 22);
        choose.Pressed += () => OnSlotClicked(slot, hasSave);
        row.AddChild(choose);

        if (hasSave)
        {
            var del = new Button
            {
                Text = Localizer.T("menu.slot.delete"),
                CustomMinimumSize = new Vector2(100, 56),
            };
            del.AddThemeFontSizeOverride("font_size", 20);
            del.Pressed += () => BeginDelete(slot);
            row.AddChild(del);
        }
        return row;
    }

    /// <summary>行内文字：档位名 + 概要（保存时间 · 已钓数）；空槽追加"空"。</summary>
    private static string SlotRowText(string slot, bool hasSave)
    {
        var name = Localizer.Tf("menu.slot.name", slot);
        if (!hasSave) return $"{name}：{Localizer.T("menu.slot.empty")}";
        var data = SaveService.Load(slot);
        if (data == null) return $"{name}：{Localizer.T("menu.slot.empty")}";
        var time = DateTimeOffset.FromUnixTimeSeconds(data.SavedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return Localizer.Tf("menu.slot.meta", name, time, data.TotalCaught);
    }

    // ---- 删除存档：二次确认（是/否，删后不可找回）----

    private void BeginDelete(string slot)
    {
        PendingDeleteSlot = slot;
        _confirmLabel.Text = Localizer.Tf("menu.slot.confirm", Localizer.Tf("menu.slot.name", slot));
        _confirmPanel.Visible = true;
    }

    /// <summary>「是」：删掉待删槽，关弹窗，刷新槽行与主按钮显隐。</summary>
    private void ConfirmDelete()
    {
        if (PendingDeleteSlot.Length == 0) { _confirmPanel.Visible = false; return; }
        SaveService.Delete(PendingDeleteSlot);
        PendingDeleteSlot = "";
        _confirmPanel.Visible = false;
        RebuildSlotRows();
        RefreshButtonVisibility();
    }

    /// <summary>设置切语言：动态文字（槽行 + 删除确认文案）跟着刷新。</summary>
    private void OnLanguageChanged()
    {
        if (_slotPanel.Visible) RebuildSlotRows();
        if (_confirmPanel.Visible && PendingDeleteSlot.Length > 0)
            _confirmLabel.Text = Localizer.Tf("menu.slot.confirm", Localizer.Tf("menu.slot.name", PendingDeleteSlot));
    }
}
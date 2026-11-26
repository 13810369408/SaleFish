using Godot;
using SaleFish.Localization;

namespace SaleFish.Main;

/// <summary>
/// 设置面板（prefab SettingsUi.tscn，主菜单「设置」弹窗里的内容）：目前只有语言选择下拉框，
/// 后续需要什么设置往这里加即可。
///  ▪ 下拉项 = 语言码（zh/en），选择即 Localizer.SetLanguage（未知码归一化到默认，见 Localizer）；
///  ▪ 面板每次打开/语言被外部改回时把选中项同步到当前语言（订阅 LanguageChanged）。
/// 语言偏好随存档走（SaveFile.Language）：主菜单改动只影响当前进程；开始新游戏时写入该槽。
/// </summary>
public sealed partial class SettingsUi : Control
{
    /// <summary>语言下拉框（OptionButton）路径。根节点就是 VBoxContainer，语言标签+下拉直挂根下。</summary>
    [Export] public NodePath? LanguageOptionPath = new("LanguageOption");

    private OptionButton? _language;

    /// <summary>支持的语言（显示名固定展示，不符语言切换；语言码传给 Localizer）。</summary>
    private static readonly (string Code, string Label)[] Languages = [("zh", "中文"), ("en", "English")];

    public override void _Ready()
    {
        _language = GetNodeOrNull<OptionButton>(LanguageOptionPath);
        if (_language == null)
        {
            GD.PushWarning("[SettingsUi] 找不到 LanguageOption（OptionButton）：语言下拉框不可用。");
            return;
        }
        _language.Clear();
        foreach (var (_, label) in Languages) _language.AddItem(label);
        SyncSelection();
        _language.ItemSelected += OnItemSelected;
        Localizer.LanguageChanged += OnLanguageChanged;
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(this))
            Localizer.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>下拉选择 → 切语言（SetLanguage 会广播，涉及字幕的本地化组件自动刷新）。</summary>
    private void OnItemSelected(long index)
    {
        if (index >= 0 && index < Languages.Length)
            Localizer.SetLanguage(Languages[index].Code);
    }

    /// <summary>外部语言变化 → 把选中项对齐当前语言（如读档把语言改回 zh）。</summary>
    private void OnLanguageChanged() => SyncSelection();

    private void SyncSelection()
    {
        if (_language == null) return;
        for (var i = 0; i < Languages.Length; i++)
        {
            if (Localizer.CurrentLanguage != Languages[i].Code) continue;
            _language.Select(i);
            return;
        }
    }
}
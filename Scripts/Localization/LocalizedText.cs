using Godot;

namespace SaleFish.Localization;

/// <summary>
/// 本地化文字<b>子组件</b>：作为文本控件的子节点挂上去（Button / Label / RichTextLabel 等都行），
/// 在检查器填 LocalizationKey，它会在游戏切语言时把父节点的 text 写成当前语言译文。
///
/// 设计成「子节点助手」而不是「挂在文本控件上的脚本」：挂在控件上会盖住控件类型（C# 侧 `as Button`
/// 取不到原生按钮、代码/测试按类型访问就断）；子节点助手不动父节点类型、只经 Call("set_text", …)
/// 写父节点 text，父节点保持原生类型、其它代码照常用 GetNode&lt;Button&gt; 操作它。
/// </summary>
public sealed partial class LocalizedText : Node
{
    /// <summary>对照表 key（如 "menu.button.menu"）；为空 = 不动。父节点需带 text 属性。</summary>
    [Export] public string LocalizationKey = "";

    public override void _Ready()
    {
        Apply();
        Localizer.LanguageChanged += Apply;
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(this))
            Localizer.LanguageChanged -= Apply;
    }

    /// <summary>把当前语言译文写给父节点的 text（经原生 set_text，任何带 text 的控件都能吃到）。</summary>
    private void Apply()
    {
        if (string.IsNullOrEmpty(LocalizationKey)) return;
        var parent = GetParent();
        if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
        parent.Call("set_text", Localizer.T(LocalizationKey));
    }
}
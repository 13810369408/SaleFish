using System;
using System.Collections.Generic;
using Godot;
using SaleFish.Fishing;
using SaleFish.Localization;
using SaleFish.Save;

namespace SaleFish.Main;

/// <summary>
/// 主菜单 UI 组件（挂在「菜单 prefab」Scenes/MenuUi.tscn 的 MenuPanel 上，根 MenuUi = Control，
/// 实例化进主关卡 UI/Hud 即成完整菜单）：节点/按钮/信号都在 prefab 里手排，本脚本只负责逻辑。
///
/// 图鉴每页布局（参考图：左右两页同款，改间距统一为 X）：
///  ▸ 上部 FishBadge（通栏圆角徽章）内 TextureRect=FishImage 显示鱼图（FishDef.ItemDef.Texture，等比居中，FishScale 缩放）；
///  ▸ 顶部 NameRow = 鱼名；徽章下方属性网格（两列等宽、统一间距 X）：
///      左列 Size / Tag(高=两格) / Hook，右列 Price / Area+Deapth / Lure，每格 = 背景 + Icon+Text。
///  ▸ 空白页（解锁列表越界）不崩：隐藏鱼图、名称占位、售价/深度清空。
///
/// 解锁规则（状态来自 FishEncyclopediaLog，属存档层，此处只是展示视图）：
///  ▪ 未见过的鱼完全没有图鉴页（页码按「已见」过滤后的列表结算，翻页不产生空白页）；
///  ▪ 已见未钓：鱼图为灰色剪影、鱼名 "???"、深度区间显示目前已探索到的所见深度（只扩不缩）；
///  ▪ 钓到过：全彩鱼图 + 真名；售价在卖鱼系统落地前一律显示 "?"（钓到也问号）。
///
/// 数据源 = 检查器 EncyclopediaFish（FishDef 数组，顺序 = 图鉴顺序，遇到新鱼按该顺序插位）；
/// 鱼名/页码经本地化 Localizer 取词，切语言自动重刷（订阅 LanguageChanged → RefreshPage）。
/// </summary>
public sealed partial class MenuUi : Control
{
    /// <summary>图鉴数据：检查器里把鱼定义（FishDef .tres）一个一个拖进来，一页两条鱼、翻页往后看。</summary>
    [Export] public FishDef[] EncyclopediaFish = [];

    /// <summary>是否把图鉴里的鱼水平镜像（TextureRect.FlipH），SpriteFrames 原图朝左、镜像后朝右。</summary>
    [Export] public bool MirrorFish = false;

    /// <summary>图鉴徽章内鱼图的大小倍率：显示尺寸 = 纹理像素尺寸 × FishScale（1 = 原尺寸、在徽章内等比显示不拉伸；>1 放大、<1 缩小），居中。</summary>
    [Export] public float FishScale = 1f;

    // ---- 场景节点引用（改了 prefab 结构，改这儿/检查器的 NodePath 即可，不影响逻辑） ----
    [Export] public NodePath? LeftPagePath = new("Center/Book/LeftPage");
    [Export] public NodePath? RightPagePath = new("Center/Book/RightPage");
    [Export] public NodePath? LeftDisplayPath = new("Center/Book/LeftPage/FishBadge/FishImage");
    [Export] public NodePath? RightDisplayPath = new("Center/Book/RightPage/FishBadge/FishImage");
    [Export] public NodePath? LeftNamePath = new("Center/Book/LeftPage/NameRow/NameText");
    [Export] public NodePath? RightNamePath = new("Center/Book/RightPage/NameRow/NameText");
    [Export] public NodePath? LeftPricePath = new("Center/Book/LeftPage/PriceRow/HBoxContainer/PriceText");
    [Export] public NodePath? RightPricePath = new("Center/Book/RightPage/PriceRow/HBoxContainer/PriceText");
    [Export] public NodePath? LeftSizePath = new("Center/Book/LeftPage/SizeRow/HBoxContainer/SizeText");
    [Export] public NodePath? RightSizePath = new("Center/Book/RightPage/SizeRow/HBoxContainer/SizeText");
    [Export] public NodePath? LeftDepthPath = new("Center/Book/LeftPage/DeapthRow/HBoxContainer/DeapthText");
    [Export] public NodePath? RightDepthPath = new("Center/Book/RightPage/DeapthRow/HBoxContainer/DeapthText");
    [Export] public NodePath? PageLabelPath = new("Center/Book/PageLabel");
    [Export] public NodePath? SettingsPanelPath = new("SettingsPanel");
    [Export] public NodePath? CloseSettingsButtonPath = new("SettingsPanel/SettingsCenter/SettingsBox/VBox/CloseButton");
    [Export] public NodePath? PrevPagePath = new("Center/Book/PrevButton");
    [Export] public NodePath? NextPagePath = new("Center/Book/NextButton");

    private TextureRect? _leftDisplay;
    private TextureRect? _rightDisplay;
    private Label? _leftName;
    private Label? _rightName;
    private Label? _leftPrice;
    private Label? _rightPrice;
    private Label? _leftSize;
    private Label? _rightSize;
    private Label? _leftDepth;
    private Label? _rightDepth;
    private Label? _pageLabel;
    private Button? _prev;
    private Button? _next;
    private Control? _leftPage;
    private Control? _rightPage;
    private Control? _settingsPanel;   // 设置弹窗（内容 = SettingsUi prefab）

    /// <summary>未钓到时鱼名显示为三个问号（钓到后显示真名）。</summary>
    private const string UnknownName = "???";
    /// <summary>售价统一显示为问号（卖鱼系统落地前不公开售价）。</summary>
    private const string UnknownPrice = "?";

    /// <summary>当前解锁的鱼（非导出，运行期状态）：EncyclopediaFish 里「已见过」的子集，保持原数组顺序。
    /// 未见过的鱼完全没有图鉴页；遇到后按这里的顺序自动出现在对应位置。RefreshPage 时从
    /// FishEncyclopediaLog 重建 —— 权威数据在存档层，这里只是图鉴的展示视图。</summary>
    private readonly List<FishDef> _unlocked = [];

    /// <summary>当前翻到第几页（从 0 起，每页两条鱼）；图为空时也为 0。</summary>
    public int CurrentPage { get; private set; }

    /// <summary>总页数：解锁（已见）鱼数 n → (n+1)/2（末页可能只有左页一条）。未解锁的鱼不计页。</summary>
    public int PageCount => (_unlocked.Count + 1) / 2;

    /// <summary>菜单当前是否打开（面板可见即打开）。</summary>
    public bool IsOpen => Visible;

    /// <summary>菜单打开/关闭通知（宿主/测试订阅用）。</summary>
    public event Action? Opened;
    public event Action? Closed;

    public override void _Ready()
    {
        Visible = false;   // 运行时默认关闭菜单（场景里不强制写 visible，方便在编辑器里手动打开预览）
        ResolveNodes();
        Localizer.LanguageChanged += OnLanguageChanged;
        FishEncyclopediaLog.Changed += OnLogChanged;   // 图鉴日志变化（见到新鱼/钓获）→ 实时重铺页面
        RefreshPage();
    }

    public override void _ExitTree()
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        FishEncyclopediaLog.Changed -= OnLogChanged;
    }

    /// <summary>语言切换 → 整页（名称/售价/页码）重新按新语言铺。</summary>
    private void OnLanguageChanged() => RefreshPage();

    /// <summary>图鉴日志变化（新鱼入图鉴/钓获/读档）→ 重铺当前页（鱼名/剪影/深度区间随状态刷新）。
    /// 日志是静态的、推送不保证本组件还在场：先判实例有效再刷新，避免操作已释放对象。</summary>
    private void OnLogChanged()
    {
        if (GodotObject.IsInstanceValid(this)) RefreshPage();
    }

    /// <summary>解析导出路径对应的节点；关键节点缺失只告警一次，不影响其余可用部分。</summary>
    private void ResolveNodes()
    {
        _leftDisplay = GetNodeOrNull<TextureRect>(LeftDisplayPath);
        _rightDisplay = GetNodeOrNull<TextureRect>(RightDisplayPath);
        _leftName = GetNodeOrNull<Label>(LeftNamePath);
        _rightName = GetNodeOrNull<Label>(RightNamePath);
        _leftPrice = GetNodeOrNull<Label>(LeftPricePath);
        _rightPrice = GetNodeOrNull<Label>(RightPricePath);
        _leftSize = GetNodeOrNull<Label>(LeftSizePath);
        _rightSize = GetNodeOrNull<Label>(RightSizePath);
        _leftDepth = GetNodeOrNull<Label>(LeftDepthPath);
        _rightDepth = GetNodeOrNull<Label>(RightDepthPath);
        _settingsPanel = GetNodeOrNull<Control>(SettingsPanelPath);
        _pageLabel = GetNodeOrNull<Label>(PageLabelPath);
        _prev = GetNodeOrNull<Button>(PrevPagePath);
        _next = GetNodeOrNull<Button>(NextPagePath);
        _leftPage = GetNodeOrNull<Control>(LeftPagePath);
        _rightPage = GetNodeOrNull<Control>(RightPagePath);
        if (_leftPage == null || _rightPage == null || _leftDisplay == null || _rightDisplay == null)
            GD.PushWarning("[MenuUi] 图鉴书页/鱼图节点缺失：请检查 MenuPanel 的导出 NodePath 是否指向场景里的书页。");
    }

    /// <summary>切换开关（右上角「菜单」按钮连接到这里）。</summary>
    public void ToggleMenu()
    {
        if (IsOpen) CloseMenu();
        else OpenMenu();
    }

    /// <summary>打开菜单（可见 + 刷新页面到当前页）。</summary>
    public void OpenMenu()
    {
        Visible = true;
        RefreshPage();
        Opened?.Invoke();
    }

    /// <summary>关闭菜单（只隐藏，页面停留当前页；下次打开仍在这一页）。顺带关掉设置弹窗（否则重开菜单会残留）。</summary>
    public void CloseMenu()
    {
        Visible = false;
        if (_settingsPanel != null) _settingsPanel.Visible = false;
        Closed?.Invoke();
    }

    /// <summary>「设置」按钮（原图鉴首页）：打开游戏内设置弹窗 —— 内容复用 SettingsUi prefab（语言下拉），
    /// 与主菜单「设置」一致。弹窗是 MenuPanel 的子节点，随菜单一起隐藏。</summary>
    public void OpenSettings()
    {
        if (_settingsPanel != null) _settingsPanel.Visible = true;
    }

    /// <summary>关闭设置弹窗（面板内「关闭」按钮）。</summary>
    public void CloseSettings()
    {
        if (_settingsPanel != null) _settingsPanel.Visible = false;
    }

    /// <summary>设置弹窗当前是否打开（测试/宿主断言用）。</summary>
    public bool IsSettingsOpen => _settingsPanel is { Visible: true };

    /// <summary>翻到下一页（下一对鱼）；已是末页无动作。</summary>
    public void NextPage()
    {
        if (CurrentPage >= PageCount - 1) return;
        CurrentPage++;
        RefreshPage();
    }

    /// <summary>翻到上一页；已是首页无动作。</summary>
    public void PrevPage()
    {
        if (CurrentPage <= 0) return;
        CurrentPage--;
        RefreshPage();
    }

    /// <summary>回到首页（第 0 页，前两条鱼）。</summary>
    public void GoToFirstPage()
    {
        if (CurrentPage == 0) return;
        CurrentPage = 0;
        RefreshPage();
    }

    /// <summary>直接跳到指定页（越界钳到合法范围；图空时无动作）。</summary>
    public void GoToPage(int page)
    {
        var clamped = Mathf.Clamp(page, 0, Mathf.Max(0, PageCount - 1));
        if (clamped == CurrentPage) return;
        CurrentPage = clamped;
        RefreshPage();
    }

    /// <summary>退出游戏（如需直接关游戏用；游戏内菜单按钮已改走 <see cref="SaveAndExit"/>）。</summary>
    public void QuitGame() => GetTree().Quit();

    /// <summary>「保存并返回主界面」（游戏内菜单退出按钮）：
    /// 先存档当前进度（SaveManager.SaveNow：当前槽/默认槽，并写 lastslot），再切回主菜单——
    /// 回到主菜单后「继续游戏」即可从刚保存的进度继续。</summary>
    public void SaveAndExit()
    {
        SaveManager.SaveNow();
        GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
    }

    /// <summary>刷新两页书：左右页摆上「当前页」的两条鱼并重算翻页按钮可用态。
    /// 数据（EncyclopediaFish）变更、翻页、开菜单、切语言后都调用它。</summary>
    public void RefreshPage()
    {
        if (_leftPage == null || _rightPage == null) return;

        // 从图鉴日志重建「当前解锁的鱼」：保持 EncyclopediaFish 数组顺序，未见过的鱼完全不出现在图鉴。
        _unlocked.Clear();
        foreach (var f in EncyclopediaFish)
            if (f != null && FishEncyclopediaLog.IsSeen(f.Id)) _unlocked.Add(f);

        // 解锁列表被改小后钳制当前页（防越界）
        CurrentPage = Mathf.Clamp(CurrentPage, 0, Mathf.Max(0, PageCount - 1));

        var i = CurrentPage * 2;
        var left = i < _unlocked.Count ? _unlocked[i] : null;
        var right = i + 1 < _unlocked.Count ? _unlocked[i + 1] : null;
        SetPage(_leftDisplay, _leftName, _leftPrice, _leftSize, _leftDepth, left);
        SetPage(_rightDisplay, _rightName, _rightPrice, _rightSize, _rightDepth, right);

        if (_pageLabel != null)
            _pageLabel.Text = PageCount == 0
                ? Localizer.T("menu.empty")
                : Localizer.Tf("menu.page", CurrentPage + 1, PageCount);
        if (_prev != null) _prev.Disabled = CurrentPage <= 0;
        if (_next != null) _next.Disabled = PageCount <= 1 || CurrentPage >= PageCount - 1;
    }

    /// <summary>把一条鱼摆到某个书页上：鱼图 = FishDef.ItemDef.Texture（背包物品贴图，徽章内 TextureRect 等比居中显示，
    /// FishScale 控制缩放）、没配时回退 SpriteFrames 第一帧静态贴图；名称行写 DisplayName/本地化名、售价行写 售价值、
    /// 尺寸行写 FishSize 档位的本地化文字。def 为空 = 空白页（隐藏鱼图、占位清空），不崩。</summary>
    private void SetPage(TextureRect? display, Label? name, Label? price, Label? size, Label? depth, FishDef? def)
    {
        if (def == null)
        {
            if (display != null) { display.Texture = null; display.Visible = false; }
            if (name != null) name.Text = Localizer.T("menu.blank");
            if (price != null) price.Text = "";
            if (size != null) size.Text = "";
            if (depth != null) depth.Text = "";
            return;
        }

        // 解锁规则：已见未钓 = 灰色剪影 + 鱼名 "???"；钓到过 = 全彩 + 真名。
        // 售价在卖鱼系统落地前一律显示 "?"（钓到也问号）。
        var caught = FishEncyclopediaLog.IsCaught(def.Id);
        var displayName = def.LocalizedName;
        if (displayName.Length == 0) displayName = Localizer.T("fish.unnamed");
        if (name != null) name.Text = caught ? displayName : UnknownName;
        if (price != null) price.Text = UnknownPrice;
        if (size != null) size.Text = Localizer.T(FishSizeKey(def.Size));
        if (depth != null)
            depth.Text = FishEncyclopediaLog.TryGetDepthRange(def.Id, out var dMin, out var dMax)
                ? $"{dMin:0}–{dMax:0}"
                : "";

        var img = def.ItemDef?.Texture ?? FirstFrame(def.SpriteFrames);   // 图鉴鱼图 = 背包物品贴图，回退精灵表首帧静态图
        if (display != null)
        {
            display.Texture = img;
            display.FlipH = MirrorFish;
            display.Visible = img != null;
            // 剪影 = 深灰调（未钓到），钓到后还原全彩。
            display.Modulate = caught ? Colors.White : new Color(0.34f, 0.35f, 0.38f, 1f);
            if (img != null)
            {
                // 显示尺寸 = 纹理像素尺寸 × FishScale，并在徽章内居中（避免 expand=1 把贴图拉伸填满徽章）
                var tw = img.GetWidth();
                var th = img.GetHeight();
                if (tw > 0 && th > 0)
                {
                    var wanted = new Vector2(tw, th) * FishScale;
                    var badgeSize = display.GetParent<Control>() is { } badge ? badge.Size : display.Size;
                    var half = (badgeSize - wanted) / 2f;
                    display.Position = half;
                    display.Size = wanted;
                }
            }
        }
    }

    /// <summary>尺寸档 → 本地化键（fishsize.&lt;档位&gt;，zh/en 在 strings.csv 里维护）。</summary>
    private static string FishSizeKey(FishSize size) => size switch
    {
        FishSize.UltraSmall => "fishsize.ultra_small",
        FishSize.Small => "fishsize.small",
        FishSize.Medium => "fishsize.medium",
        FishSize.Large => "fishsize.large",
        FishSize.UltraLarge => "fishsize.ultra_large",
        _ => "fishsize.medium",
    };

    /// <summary>精灵表第一帧贴图（回退用）：取第一个动画的第一帧当静态展示图；没有精灵表 = null。</summary>
    private static Texture2D? FirstFrame(SpriteFrames? frames)
    {
        if (frames == null) return null;
        var anims = frames.GetAnimationNames();
        if (anims.Length == 0) return null;
        return frames.GetFrameTexture(anims[0], 0);
    }
}
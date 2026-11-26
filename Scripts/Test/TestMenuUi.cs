using Godot;
using SaleFish.Fishing;
using SaleFish.Inventory;
using SaleFish.Main;
using SaleFish.Save;

namespace SaleFish.Test;

/// <summary>
/// 主菜单（右上角「菜单」按钮 + 图鉴两页书）交互测试：实例化正式场景 scenes/main_stage.tscn，
/// 菜单页与全部控件都来自 prefab Scenes/MenuUi.tscn（不代码创建）。
/// 每页布局（左右页同款）：顶部 NameRow 鱼名 + FishBadge 徽章(内 FishImage TextureRect 显示鱼图) +
/// 属性网格（Size/Tag/Hook 左列，Price/Area+Deapth/Lure 右列，每格 Icon+Text）。
/// 图鉴鱼图读取 FishDef.ItemDef.Texture（背包物品贴图）：优先它、未配时回退 SpriteFrames 第一帧静态图，
/// MirrorFish 控制 FlipH 镜像；FishScale 控制徽章内鱼图缩放。逐条验证：
///  ▪ 右上角「菜单」按钮存在；初始菜单面板隐藏；图鉴预填的鱼（Sartine.tres，未配展示图 → 走回退）
///    在第 0 页左页显示静态鱼图、名称/售价信息条、右页（奇数鱼）空白占位、翻页按钮按页数置灰；
///  ▪ 按钮 pressed 切换开/关（连接走 prefab [connection]，这里经 EmitSignal 触发）；
///  ▪ 图鉴数据源 = 检查器 EncyclopediaFish 数组：运行期替换成若干条合成鱼，翻页/回首页/钳制、
///    末页右页空白、翻页按钮可用态；
///  ▪ 展示图优先 ItemDef.Texture、未配回退精灵表首帧；MirrorFish 控制 FlipH 镜像；
///  ▪ 「继续游戏」按钮关闭菜单；重开菜单内容保留；
///  ▪ 图鉴置空 → 「图鉴为空」占位 + 双页隐藏，不崩。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestMenuUi.tscn --quit-after 240
/// </summary>
public sealed partial class TestMenuUi : Node
{
    private int _passed;
    private int _failed;

    private MainStage _stage = null!;
    private MenuUi _menu = null!;
    private Button _menuButton = null!;
    private Button _continueButton = null!;
    private Button _settingsBtn = null!;
    private Button _quitBtn = null!;
    private Button _prev = null!;
    private Button _next = null!;
    private TextureRect _left = null!;
    private TextureRect _right = null!;
    private Label _leftName = null!;
    private Label _rightName = null!;
    private Label _leftPrice = null!;
    private Label _rightPrice = null!;
    private Label _leftSize = null!;
    private Label _rightSize = null!;
    private Label _leftDepth = null!;
    private Label _rightDepth = null!;
    private Label _pageLabel = null!;

    // 合成测试鱼：f1..f4 配背包物品贴图（ItemDef.Texture），f5 只给精灵表（走回退首帧）；各指定不同尺寸档。
    private static readonly FishDef _f1 = MakeFish(1, 16, 9, "甲鱼", 10f, FishSize.Medium, withImage: true);
    private static readonly FishDef _f2 = MakeFish(2, 32, 18, "乙鱼", 20f, FishSize.Large, withImage: true);
    private static readonly FishDef _f3 = MakeFish(3, 48, 36, "丙鱼", 30f, FishSize.Small, withImage: true);
    private static readonly FishDef _f4 = MakeFish(4, 16, 9, "丁鱼", 40f, FishSize.UltraSmall, withImage: true);
    private static readonly FishDef _f5 = MakeFish(5, 32, 18, "戊鱼", 50f, FishSize.UltraLarge, withImage: false);

    public override async void _Ready()
    {
        SeedPrefabFishForUnlock();   // 预置图鉴解锁状态：必须早于舞台实例化（MenuUi._Ready 会按日志过滤/点亮）
        // 开局阶段随存档走：本测试以「准备钓鱼」(Ready) 为前提。
        SaleFish.Save.SaveManager.InFreeMove = false;
        _stage = ResourceLoader.Load<PackedScene>("res://Scenes/MainStage.tscn")
            .Instantiate<MainStage>();
        AddChild(_stage);

        _menuButton = _stage.GetNode<Button>("UI/Hud/MenuUi/MenuButton");
        _menu = _stage.GetNode<MenuUi>("UI/Hud/MenuUi/MenuPanel");
        _continueButton = _stage.GetNode<Button>("UI/Hud/MenuUi/MenuPanel/Center/Book/MenuButtons/ContinueButton");
        _settingsBtn = _stage.GetNode<Button>("UI/Hud/MenuUi/MenuPanel/Center/Book/MenuButtons/SettingsButton");
        _quitBtn = _stage.GetNode<Button>("UI/Hud/MenuUi/MenuPanel/Center/Book/MenuButtons/QuitButton");
        _prev = _stage.GetNode<Button>("UI/Hud/MenuUi/MenuPanel/Center/Book/PrevButton");
        _next = _stage.GetNode<Button>("UI/Hud/MenuUi/MenuPanel/Center/Book/NextButton");
        _left = _stage.GetNode<TextureRect>("UI/Hud/MenuUi/MenuPanel/Center/Book/LeftPage/FishBadge/FishImage");
        _right = _stage.GetNode<TextureRect>("UI/Hud/MenuUi/MenuPanel/Center/Book/RightPage/FishBadge/FishImage");
        _leftName = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/LeftPage/NameRow/NameText");
        _rightName = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/RightPage/NameRow/NameText");
        _leftPrice = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/LeftPage/PriceRow/HBoxContainer/PriceText");
        _rightPrice = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/RightPage/PriceRow/HBoxContainer/PriceText");
        _leftSize = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/LeftPage/SizeRow/HBoxContainer/SizeText");
        _rightSize = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/RightPage/SizeRow/HBoxContainer/SizeText");
        _leftDepth = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/LeftPage/DeapthRow/HBoxContainer/DeapthText");
        _rightDepth = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/RightPage/DeapthRow/HBoxContainer/DeapthText");
        _pageLabel = _stage.GetNode<Label>("UI/Hud/MenuUi/MenuPanel/Center/Book/PageLabel");

        // 等两帧让容器布局稳定（布局断言在此之后才有的；逻辑断言不依赖）
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Run();
        GD.Print($"[MenuUiTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[MenuUiTest] ALL PASSED" : "[MenuUiTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Run()
    {
        // 前面等待的 2 帧里真实鱼可能已被相机「见到」并追加过深度（污染预置区间）。
        // Run 体内无 await → 不会再跑 _Process → 这里重新定死状态后断言全程稳定。
        SeedPrefabFishForUnlock();
        _menu.RefreshPage();

        // ---- 1. 初始态：按钮存在、面板隐藏、预填的图鉴在第 0 页 ----
        Check("右上角「菜单」按钮存在", _menuButton != null && _menuButton.Text == "菜单");
        Check("「保存并返回主界面」退出按钮：文字已本地化",
            _quitBtn != null && _quitBtn.Text == "保存并返回主界面");
        Check("菜单面板初始隐藏", !_menu.Visible);
        var sartine = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/Sartine.tres");
        Check("图鉴预填：数组 = 沙丁鱼 + 海鲈鱼 + 角箱魨 + 翻车鱼（4 条）",
            _menu.EncyclopediaFish.Length == 4 && ReferenceEquals(_menu.EncyclopediaFish[0], sartine));
        Check("图鉴预填：4 条 → 总页数 2", _menu.PageCount == 2);
        Check("图鉴预填：左页沙丁鱼鱼图用背包物品贴图",
            _left.Visible && _left.Texture == sartine!.ItemDef?.Texture);
        Check("图鉴预填：左页 名称=? 售价/尺寸 = 已钓 沙丁鱼 / 售价 ? / 超小型",
            _leftName.Text == "沙丁鱼" && _leftPrice.Text == "?" && _leftSize.Text == "超小型");
        Check("图鉴预填：左页已钓 → 全彩（Modulate 白）", _left.Modulate == Colors.White);
        Check("图鉴预填：左页深度行 = 已探索区间 60–80", _leftDepth.Text == "60–80");
        var seaBassItem = ResourceLoader.Load<PlacementItemTypeDef>("res://Data/Inventory/Items/SeaBassItem.tres");
        Check("图鉴预填：右页 = 海鲈鱼（背包物品贴图显示，名称/? /尺寸有值）",
            _right.Visible && _right.Texture == seaBassItem?.Texture
            && _rightName.Text == "海鲈鱼" && _rightPrice.Text == "?" && _rightSize.Text == "大型");
        Check("图鉴预填：右页深度行 = 已探索区间 90–120", _rightDepth.Text == "90–120");
        Press(_next);   // 第 1 页：角箱魨 / 翻车鱼（预置为已见未钓）
        Check("已见未钓页：角箱魨 名称 ???（未钓）", _leftName.Text == "???");
        Check("已见未钓页：角箱魨 灰色剪影（Modulate 非白）", _left.Modulate != Colors.White);
        Check("已见未钓页：翻车鱼 名称 ???", _rightName.Text == "???");
        Press(_prev);   // 回第 0 页，继续后续流程
        Check("已见未钓页：已回第 0 页", _menu.CurrentPage == 0);
        Check("图鉴预填：首页 上一页禁用、下一页可用", _prev.Disabled && !_next.Disabled);
        Check("图鉴预填：页脚文字 = 第 1 / 2 页", _pageLabel.Text == "第 1 / 2 页");

        // ---- 2. 右上角按钮切换开/关（走 prefab 里连接的 ToggleMenu） ----
        Press(_menuButton);
        Check("点「菜单」按钮：菜单打开", _menu.IsOpen);
        Check("点「菜单」按钮再点：菜单关闭", PressSafely(_menuButton) && !_menu.IsOpen);
        Press(_menuButton);
        Check("再次打开：仍显示图鉴第 0 页", _menu.IsOpen && _menu.CurrentPage == 0);

        // ---- 3. 替换图鉴数据（= 检查器数组）：先只解锁 f1/f3/f5 —— 验证「未解锁的鱼完全不占页」----
        FishEncyclopediaLog.ClearAll();
        FishEncyclopediaLog.See(_f1.Id, 10f); FishEncyclopediaLog.Caught(_f1.Id);
        FishEncyclopediaLog.See(_f3.Id, 20f);
        FishEncyclopediaLog.See(_f5.Id, 50f);
        _menu.EncyclopediaFish = new[] { _f1, _f2, _f3, _f4, _f5 };
        _menu.RefreshPage();
        Check("只解锁 f1/f3/f5：总页数 2（f2/f4 未解锁不占页）", _menu.PageCount == 2);
        Check("第 0 页：左=f1（已钓真名甲鱼）、右=f3（已见未钓 ???）",
            _left.Visible && _left.Texture == _f1.ItemDef!.Texture && _leftName.Text == "甲鱼"
            && _right.Visible && _right.Texture == _f3.ItemDef!.Texture && _rightName.Text == "???");
        Check("f3 已见未钓：灰色剪影", _right.Modulate != Colors.White);
        Check("未解锁的 f2/f4：没有它们的页面（页脚 第 1 / 2 页）", _pageLabel.Text == "第 1 / 2 页");
        // 补解锁全部 → 恢复「5 条 / 3 页」流程
        FishEncyclopediaLog.See(_f2.Id, 20f); FishEncyclopediaLog.Caught(_f2.Id);
        FishEncyclopediaLog.Caught(_f3.Id);   // 后续流程需要 f3 显示真名（丙鱼）
        FishEncyclopediaLog.See(_f4.Id, 40f); FishEncyclopediaLog.Caught(_f4.Id);
        FishEncyclopediaLog.Caught(_f5.Id);
        _menu.RefreshPage();
        Check("补解锁全部：总页数 3", _menu.PageCount == 3);
        Check("补解锁后当前页钳制在首页（第 0 页）", _menu.CurrentPage == 0);
        Check("第 0 页：左页 = 第 1 条、右页 = 第 2 条（各自背包物品贴图）",
            _left.Visible && _left.Texture == _f1.ItemDef!.Texture
            && _right.Visible && _right.Texture == _f2.ItemDef!.Texture);
        Check("第 0 页：名称/售价信息条（甲鱼=? / 乙鱼=?）",
            _leftName.Text == "甲鱼" && _leftPrice.Text == "?"
            && _rightName.Text == "乙鱼" && _rightPrice.Text == "?");
        Check("第 0 页：尺寸条（甲鱼=中型 / 乙鱼=大型）", _leftSize.Text == "中型" && _rightSize.Text == "大型");
        Check("第 0 页：上一页禁用、下一页可用", _prev.Disabled && !_next.Disabled);
        Check("页脚文字 = 第 1 / 3 页", _pageLabel.Text == "第 1 / 3 页");

        // ---- 4. 翻页：下一页显示第 3/4 条，末页左页 = 未配展示图的回退静态图 ----
        Press(_next);
        Check("下一页：到第 1 页（第 3/4 条）",
            _menu.CurrentPage == 1 && _left.Texture == _f3.ItemDef!.Texture && _right.Texture == _f4.ItemDef!.Texture);
        Check("第 1 页：名称/售价信息条（丙鱼=? / 丁鱼=?）",
            _leftName.Text == "丙鱼" && _leftPrice.Text == "?" && _rightName.Text == "丁鱼");
        Check("第 1 页：尺寸条（丙鱼=小型 / 丁鱼=超小型）", _leftSize.Text == "小型" && _rightSize.Text == "超小型");
        Check("第 1 页：上/下一页都可用", !_prev.Disabled && !_next.Disabled);
        Check("页脚文字 = 第 2 / 3 页", _pageLabel.Text == "第 2 / 3 页");
        Press(_next);
        Check("下一页：到第 2 页（第 5 条回退精灵表首帧 + 空白右页）",
            _menu.CurrentPage == 2 && _left.Texture == FirstFrameTex(_f5)
            && !_right.Visible && _rightName.Text == "(空白)" && _rightPrice.Text == "");
        Check("末页：尺寸条（戊鱼=超大型）", _leftSize.Text == "超大型");
        Check("末页：下一页禁用", _next.Disabled);
        Press(_next);
        Check("末页再点下一页：不动（钳制）", _menu.CurrentPage == 2);

        // ---- 5. 回翻 + 回首页（方法回首页；「设置」按钮改开设置弹窗而非回首页） ----
        Press(_prev);
        Check("上一页：回第 1 页", _menu.CurrentPage == 1);
        _menu.GoToFirstPage();
        Check("回首页：第 0 页，页脚 第 1 / 3 页", _menu.CurrentPage == 0 && _pageLabel.Text == "第 1 / 3 页");
        Press(_settingsBtn);
        Check("「设置」按钮：打开设置弹窗（页码不动）", _menu.IsSettingsOpen && _menu.CurrentPage == 0);
        Check("设置弹窗：复用 SettingsUi prefab（语言下拉挂载）",
            _stage.GetNodeOrNull<OptionButton>("UI/Hud/MenuUi/MenuPanel/SettingsPanel/SettingsCenter/SettingsBox/VBox/SettingsUi/LanguageOption") != null);
        Press(_stage.GetNode<Button>("UI/Hud/MenuUi/MenuPanel/SettingsPanel/SettingsCenter/SettingsBox/VBox/CloseButton"));
        Check("设置弹窗：面板内关闭按钮关闭", !_menu.IsSettingsOpen);

        // ---- 6. 「继续游戏」关闭；重开内容保留 ----
        Press(_continueButton);
        Check("「继续游戏」：菜单关闭", !_menu.IsOpen);
        Press(_menuButton);
        Check("重开：内容保留（第 0 页左页=第 1 条背包物品贴图）",
            _menu.IsOpen && _left.Visible && _left.Texture == _f1.ItemDef!.Texture);

        // ---- 7. 镜像开关：MirrorFish 控制徽章鱼图的 FlipH ----
        _menu.MirrorFish = true;
        _menu.RefreshPage();
        Check("MirrorFish：开启后鱼图水平镜像（FlipH=true）", _left.FlipH && _right.FlipH);
        _menu.MirrorFish = false;
        _menu.RefreshPage();
        Check("MirrorFish：关闭后恢复未镜像（FlipH=false）", !_left.FlipH && !_right.FlipH);

        // ---- 7b. FishScale：显示尺寸 = 纹理像素 × FishScale，徽章内居中 ----
        // 注意 prefab 里 FishScale 配的是 6.0，这里显式设 1 再断言（不依赖场景默认值）。
        _menu.FishScale = 1f;
        _menu.RefreshPage();
        Check("FishScale=1：鱼图尺寸 = 纹理像素（左 16×9 / 右 32×18）",
            _left.Size == new Vector2(16f, 9f) && _right.Size == new Vector2(32f, 18f));
        Check("鱼图居中于徽章",
            _left.Position.DistanceTo((_left.GetParent<Control>().Size - _left.Size) / 2f) < 0.01f);
        _menu.FishScale = 2f;
        _menu.RefreshPage();
        Check("FishScale=2：鱼图尺寸 ×2（左 32×18 / 右 64×36）",
            _left.Size == new Vector2(32f, 18f) && _right.Size == new Vector2(64f, 36f));
        _menu.FishScale = 1f;
        _menu.RefreshPage();

        // ---- 8. 清空图鉴：占位 + 双页隐藏，不崩 ----
        _menu.EncyclopediaFish = [];
        _menu.RefreshPage();
        Check("清空图鉴：总页数 0、占位文字", _menu.PageCount == 0 && _pageLabel.Text == "图鉴为空");
        Check("清空图鉴：左右页都隐藏鱼图 + 空白占位",
            !_left.Visible && !_right.Visible
            && _leftName.Text == "(空白)" && _rightName.Text == "(空白)"
            && _leftPrice.Text == "" && _rightPrice.Text == "");
        Check("清空图鉴：翻页按钮全部禁用", _prev.Disabled && _next.Disabled);
    }

    /// <summary>派发按钮 pressed 信号（等价真实点击，headless 下不经过输入队列）；节点缺失时忽略（由前置断言暴露）。</summary>
    private static void Press(Button? b)
    {
        if (b == null || !GodotObject.IsInstanceValid(b)) return;
        b.EmitSignal(BaseButton.SignalName.Pressed);
    }

    /// <summary>按下再确认按钮仍有效（返回 true 供链式断言）。</summary>
    private static bool PressSafely(Button? b)
    {
        Press(b);
        return b != null && GodotObject.IsInstanceValid(b);
    }

    /// <summary>鱼定义精灵表第一帧贴图（图鉴未配展示图时的回退显示）。</summary>
    private static Texture2D FirstFrameTex(FishDef def)
    {
        var anims = def.SpriteFrames!.GetAnimationNames();
        return def.SpriteFrames.GetFrameTexture(anims[0], 0);
    }

    /// <summary>造一条合成测试鱼：精灵 = 指定像素尺寸的纯色贴图（一个动画一帧），可选是否配背包物品贴图
    /// ItemDef.Texture（false = 只给精灵表，走「未配物品贴图 → 回退首帧」路径），名称/售价/尺寸档可设。</summary>
    private static FishDef MakeFish(long id, int w, int h, string name, float price, FishSize size, bool withImage)
    {
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0.3f, 0.6f, 0.9f, 1f));
        var tex = ImageTexture.CreateFromImage(img);
        var frames = new SpriteFrames();
        if (!frames.HasAnimation("default")) frames.AddAnimation("default");   // 引擎新建的 SpriteFrames 自带 default，重复 Add 会打告警
        frames.AddFrame("default", tex);
        return new FishDef
        {
            Id = id,
            DisplayName = name,
            SellPrice = price,
            Size = size,
            SpriteFrames = frames,
            ItemDef = withImage ? new PlacementItemTypeDef { DisplayName = name, Texture = tex } : null,
        };
    }

    /// <summary>预置图鉴解锁状态（测试环境的「已玩出进度」）：4 条预填鱼全部见过；
    /// 沙丁鱼/海鲈鱼钓到过（真名+全彩），角箱魨/翻车鱼已见未钓（灰剪影 "???"）。
    /// 深度行顺带造出「已探索区间」：沙丁鱼 [60,80]、海鲈鱼 [90,120]（两次见到只扩不缩）。</summary>
    private static void SeedPrefabFishForUnlock()
    {
        var sartine = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/Sartine.tres");
        var seaBass = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/SeaBass.tres");
        var boxfish = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/YellowBoxfish.tres");
        var mola = ResourceLoader.Load<FishDef>("res://Data/Fishing/Fish/MolaMola.tres");

        FishEncyclopediaLog.ClearAll();
        if (sartine != null) { FishEncyclopediaLog.See(sartine.Id, 80f); FishEncyclopediaLog.See(sartine.Id, 60f); FishEncyclopediaLog.Caught(sartine.Id); }
        if (seaBass != null) { FishEncyclopediaLog.See(seaBass.Id, 120f); FishEncyclopediaLog.See(seaBass.Id, 90f); FishEncyclopediaLog.Caught(seaBass.Id); }
        if (boxfish != null) FishEncyclopediaLog.See(boxfish.Id, 200f);
        if (mola != null) FishEncyclopediaLog.See(mola.Id, 40f);
    }

    private void Check(string name, bool cond) => Check(name, cond, null);

    private void Check(string name, bool cond, string? detail)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print(detail == null ? $"[FAIL] {name}" : $"[FAIL] {name} —— {detail}"); }
    }
}
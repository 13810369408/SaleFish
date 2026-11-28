using System;
using System.Collections.Generic;
using Godot;
using SaleFish.Localization;
using SaleFish.Save;

namespace SaleFish.Shop;

/// <summary>
/// 鱼店 UI（prefab：Scenes/ShopUi.tscn，实例挂在 MainStage 的 UI/Hud 下）：
///  ▪ 「开始卖鱼」按钮（右下角，仅准备开店阶段显示）；
///  ▪ 价格板面板：按鱼种一行（鱼名 / 历史最高成交价(可选) / 0~999 数字输入框）；
///  ▪ 结算面板：总收入 + 每种鱼卖出/剩余条数，可展开「详细信息」（每个顾客买了什么）；
///  ▪ 两个模态：确认(是/否) 与 警告(只能关闭)。
/// 只负责展示与收集输入；卖鱼规则、阶段机、金币/库存改动全部在 MainStage + SellService。
/// 玩家标价直接写进 <see cref="SaveManager.ShopPrices"/>（归档随存档保存）。
/// </summary>
public sealed partial class ShopUi : Control
{
    /// <summary>价格板里的一行（鱼种）：名字 + 图鉴里的历史最高成交价（没卖过 = null）。</summary>
    public readonly record struct PriceRow(long FishId, string Name, int? HistoricalPrice);

    private Panel _pricePanel = null!;
    private VBoxContainer _priceRows = null!;
    private Panel _resultPanel = null!;
    private Label _resultSummary = null!;
    private VBoxContainer _resultDetail = null!;
    private ScrollContainer _resultScroll = null!;
    private Control _confirmOverlay = null!;
    private Label _confirmPrompt = null!;
    private Control _warnOverlay = null!;
    private Label _warnMessage = null!;

    private bool _detailShown;
    private readonly Dictionary<long, LineEdit> _editors = [];

    /// <summary>点价格板下方的「开始开店」（开始卖鱼）。</summary>
    public event Action? StartSellPressed;
    /// <summary>价格板被关闭（宿主可据此刷新状态栏）。</summary>
    public event Action? PriceBoardClosed;
    /// <summary>结算面板点「结束」。</summary>
    public event Action? ResultClosed;
    /// <summary>确认弹窗点「是」/「否」。</summary>
    public event Action? ConfirmAccepted;
    public event Action? ConfirmCancelled;
    /// <summary>警告弹窗（只能关闭）被关闭。</summary>
    public event Action? WarningClosed;

    /// <summary>价格板是否开着（宿主用它决定点击路由）。</summary>
    public bool PriceBoardOpen => _pricePanel.Visible;

    /// <summary>结算面板是否开着。</summary>
    public bool ResultOpen => _resultPanel.Visible;

    /// <summary>确认/警告弹窗是否开着（模态期间宿主不应放行世界点击）。</summary>
    public bool ModalOpen => _confirmOverlay.Visible || _warnOverlay.Visible;

    public override void _Ready()
    {
        _pricePanel = GetNode<Panel>("PricePanel");
        _priceRows = GetNode<VBoxContainer>("PricePanel/VBox/Scroll/Rows");
        _resultPanel = GetNode<Panel>("ResultPanel");
        _resultSummary = GetNode<Label>("ResultPanel/VBox/Summary");
        _resultDetail = GetNode<VBoxContainer>("ResultPanel/VBox/DetailScroll/DetailText");
        _resultScroll = GetNode<ScrollContainer>("ResultPanel/VBox/DetailScroll");
        _confirmOverlay = GetNode<Control>("ConfirmOverlay");
        _confirmPrompt = GetNode<Label>("ConfirmOverlay/Center/Panel/VBox/Prompt");
        _warnOverlay = GetNode<Control>("WarnOverlay");
        _warnMessage = GetNode<Label>("WarnOverlay/Center/Panel/VBox/Message");

        // 价格板下方的按钮 = 「开始开店」（开始卖鱼）：准备开店阶段玩家不能关页面，只能定价后点它开卖。
        GetNode<Button>("PricePanel/VBox/StartShopButton").Pressed += () => StartSellPressed?.Invoke();
        GetNode<Button>("ResultPanel/VBox/DetailButton").Pressed += ToggleDetail;
        GetNode<Button>("ResultPanel/VBox/CloseButton").Pressed += () => ResultClosed?.Invoke();

        GetNode<Button>("ConfirmOverlay/Center/Panel/VBox/Buttons/YesButton").Pressed += () =>
        {
            _confirmOverlay.Hide();
            ConfirmAccepted?.Invoke();
        };
        GetNode<Button>("ConfirmOverlay/Center/Panel/VBox/Buttons/NoButton").Pressed += () =>
        {
            _confirmOverlay.Hide();
            ConfirmCancelled?.Invoke();
        };
        GetNode<Button>("WarnOverlay/Center/Panel/VBox/CloseButton").Pressed += () =>
        {
            _warnOverlay.Hide();
            WarningClosed?.Invoke();
        };

        _pricePanel.Hide();
        _resultPanel.Hide();
        _confirmOverlay.Hide();
        _warnOverlay.Hide();
    }

    /// <summary>打开/刷新价格板：按给定的鱼种行重建（已填价格从 SaveManager.ShopPrices 回填）。</summary>
    public void OpenPriceBoard(IReadOnlyList<PriceRow> rows)
    {
        BuildPriceRows(rows);
        _pricePanel.Show();
    }

    /// <summary>价格板开着时，仓库内容变了 → 重建行（已填价格保留：值存在 SaveManager.ShopPrices 里）。</summary>
    public void RefreshPriceBoard(IReadOnlyList<PriceRow> rows)
    {
        if (!_pricePanel.Visible) return;
        BuildPriceRows(rows);
    }

    public void ClosePriceBoard()
    {
        if (!_pricePanel.Visible) return;
        _pricePanel.Hide();
        PriceBoardClosed?.Invoke();
    }

    private void BuildPriceRows(IReadOnlyList<PriceRow> rows)
    {
        foreach (var child in _priceRows.GetChildren())
        {
            _priceRows.RemoveChild(child);
            child.QueueFree();
        }
        _editors.Clear();

        foreach (var row in rows)
        {
            var hb = new HBoxContainer { Name = $"Fish{row.FishId}", CustomMinimumSize = new Vector2(0, 52) };
            hb.AddThemeConstantOverride("separation", 16);
            _priceRows.AddChild(hb);

            var name = new Label
            {
                Text = row.Name,
                CustomMinimumSize = new Vector2(200, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            name.AddThemeFontSizeOverride("font_size", 24);
            hb.AddChild(name);

            if (row.HistoricalPrice is { } historical)
            {
                var hist = new Label
                {
                    Text = Localizer.Tf("shop.price.historical", historical),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                hist.AddThemeFontSizeOverride("font_size", 18);
                hist.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
                hb.AddChild(hist);
            }

            var edit = new LineEdit
            {
                Name = "Price",
                MaxLength = 3,                                  // 最多三位
                Alignment = HorizontalAlignment.Right,
                CustomMinimumSize = new Vector2(120, 44),
                PlaceholderText = Localizer.T("shop.price.placeholder"),
            };
            edit.AddThemeFontSizeOverride("font_size", 24);
            if (SaveManager.ShopPrices.TryGetValue(row.FishId, out var price) && price > 0)
                edit.Text = price.ToString();

            var fishId = row.FishId;
            edit.TextChanged += text => OnPriceTextChanged(fishId, edit, text);
            hb.AddChild(edit);
            _editors[fishId] = edit;

            var coin = new Label { Text = Localizer.T("shop.price.unit"), VerticalAlignment = VerticalAlignment.Center };
            coin.AddThemeFontSizeOverride("font_size", 20);
            hb.AddChild(coin);
        }
    }

    /// <summary>只允许数字、最多三位；空 = 未标价（从 SaveManager.ShopPrices 里删掉）。</summary>
    private static void OnPriceTextChanged(long fishId, LineEdit edit, string text)
    {
        var digits = new System.Text.StringBuilder(3);
        foreach (var ch in text)
        {
            if (ch is < '0' or > '9') continue;
            digits.Append(ch);
            if (digits.Length == 3) break;
        }
        var cleaned = digits.ToString();
        if (cleaned != text)
        {
            var caret = edit.CaretColumn;
            edit.Text = cleaned;
            edit.CaretColumn = Mathf.Min(caret, cleaned.Length);
        }
        if (cleaned.Length == 0 || !int.TryParse(cleaned, out var value))
            SaveManager.ShopPrices.Remove(fishId);
        else
            SaveManager.ShopPrices[fishId] = value;
    }

    /// <summary>显示确认弹窗（是/否）。</summary>
    public void AskConfirm(string message)
    {
        _confirmPrompt.Text = message;
        _confirmOverlay.Show();
    }

    /// <summary>显示只能关闭的警告弹窗。</summary>
    public void ShowWarning(string message)
    {
        _warnMessage.Text = message;
        _warnOverlay.Show();
    }

    /// <summary>显示结算面板：summary = 总收入/各鱼卖出与剩余；details = 每个顾客的购买明细（默认折叠）。</summary>
    public void ShowResult(string summary, string details, bool hasDetail)
    {
        _resultSummary.Text = summary;
        BuildDetail(details);
        _detailShown = false;
        _resultScroll.Hide();
        GetNode<Button>("ResultPanel/VBox/DetailButton").Visible = hasDetail;
        _resultPanel.Show();
    }

    private void BuildDetail(string details)
    {
        foreach (var child in _resultDetail.GetChildren())
        {
            _resultDetail.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var line in details.Split('\n'))
        {
            if (line.Length == 0) continue;
            var label = new Label { Text = line, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            label.AddThemeFontSizeOverride("font_size", 18);
            _resultDetail.AddChild(label);
        }
    }

    private void ToggleDetail()
    {
        _detailShown = !_detailShown;
        _resultScroll.Visible = _detailShown;
    }

    /// <summary>关掉结算面板（宿主在「结束」后切阶段）。</summary>
    public void HideResult()
    {
        _resultPanel.Hide();
        _resultScroll.Hide();
        _detailShown = false;
    }
}

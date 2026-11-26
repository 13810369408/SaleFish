using System.Collections.Generic;
using Godot;
using SaleFish.Inventory;
using SaleFish.Localization;
using SaleFish.View;

namespace SaleFish.Main;

/// <summary>
/// 背包测试关卡（Scenes/InventoryDemo.tscn）：承接从 MainStage 拆出来的「获取放置物 / 丢弃放置物」。
/// 内容与原 MainStage 对应逻辑一致：随机物品池 + 全局手（HeldItemController）+ 宝箱仓库按钮
/// （WarehouseButton，位置记忆 / 拖拽照旧）+ 状态栏。主关卡现在没有这两个按钮了。
/// 操作：点「获取放置物」随机拿一件到鼠标（格大小取仓库配置）→ 点进仓库放/拾 · 右键旋转 · 「丢弃」清空手持。
/// </summary>
public sealed partial class InventoryDemo : Node2D
{
    /// <summary>仓库布局配置：打开时据此创建（InventoryView.CreateStorage）；为空回退 res://Data/Inventory/Storage/MainBag.tres。</summary>
    [Export] public StorageTypeDef? Storage = null;

    /// <summary>「获取放置物」的随机池：在检查器里自由增删；为空时回退扫目录加载全部配置。</summary>
    [Export] public PlacementItemTypeDef[] RandomPool = [];

    private readonly RandomNumberGenerator _rng = new();
    private List<PlacementItemTypeDef> _defs = null!;
    private StorageTypeDef? _storageDef;
    private Control? _hud;
    private Button _getButton = null!;
    private Button _discardButton = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        GetWindow().Mode = Window.ModeEnum.Maximized;
        _rng.Randomize();
        _defs = RandomPool.Length > 0 ? [.. RandomPool] : LoadItemDefs();
        _storageDef = Storage ?? ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/MainBag.tres");
        _hud = GetNodeOrNull<Control>("UI/Hud");

        BuildUi();
        HookWarehouseButtons();
        HeldItemController.Instance.HeldChanged += OnHeldChanged;
        OnHeldChanged(null);
    }

    public override void _ExitTree()
    {
        if (HeldItemController.Instance != null)
            HeldItemController.Instance.HeldChanged -= OnHeldChanged;
    }

    private void BuildUi()
    {
        Node ui = _hud as Node ?? this;

        var bar = new HBoxContainer { Position = new Vector2(16, 18) };
        bar.AddThemeConstantOverride("separation", 10);
        ui.AddChild(bar);

        _getButton = new Button { Name = "GetButton", Text = Localizer.T("ui.get") };
        _getButton.Pressed += OnGetPressed;
        bar.AddChild(_getButton);

        _discardButton = new Button { Name = "DiscardButton", Text = Localizer.T("ui.discard") };
        _discardButton.Pressed += OnDiscardPressed;
        bar.AddChild(_discardButton);

        _status = new Label { Name = "Status", Position = new Vector2(16, 60), Text = "" };
        ui.AddChild(_status);

        RefreshButtons();
    }

    /// <summary>获取=手持时禁用；丢弃=仅手持时出现。</summary>
    private void RefreshButtons()
    {
        var holding = HeldItemController.Instance?.HeldItem != null;
        if (_getButton != null) _getButton.Disabled = holding;
        if (_discardButton != null) _discardButton.Visible = holding;
    }

    private void HookWarehouseButtons()
    {
        foreach (var n in FindChildren("*", recursive: true, owned: false))
        {
            if (n is not WarehouseButton b) continue;
            b.Opened += () =>
            {
                if (GodotObject.IsInstanceValid(b) && GodotObject.IsInstanceValid(_status))
                    _status.Text = Localizer.Tf("status.warehouse.opened", b.StorageDef?.DisplayName ?? "");
            };
            b.Closed += () =>
            {
                if (GodotObject.IsInstanceValid(_status))
                    _status.Text = Localizer.T("status.warehouse.closed");
            };
            b.Moved += () =>
            {
                if (GodotObject.IsInstanceValid(_status))
                    _status.Text = Localizer.T("status.warehouse.moved");
            };
        }
    }

    /// <summary>「获取放置物」：随机取一个已配置类型交给全局手（生成在鼠标上）。手持时按钮已禁用，逻辑再兜底一次。</summary>
    private void OnGetPressed()
    {
        if (HeldItemController.Instance?.HeldItem != null) return;
        if (_defs.Count == 0)
        {
            _status.Text = Localizer.T("status.get.empty");
            return;
        }
        var def = _defs[(int)_rng.RandiRange(0, _defs.Count - 1)];
        // 格大小始终取仓库配置（即使仓库尚未打开，手持/落仓保持一致）；
        // anchorView 取当前最新打开的仓库（解析初始抓手锚点用），无则留给手回退默认锚点。
        HeldItemController.Instance!.TakeExternal(def.ToItem(), WarehouseRegistry.LastView(),
            _storageDef?.CellSize);
    }

    /// <summary>「丢弃放置物」：清空全局手持；仓库与已放置的物品不受影响。</summary>
    private void OnDiscardPressed() => HeldItemController.Instance?.DiscardHeld();

    private void OnHeldChanged(InventoryItem? held)
    {
        RefreshButtons();
        if (held == null)
        {
            _status.Text = Localizer.T("status.empty");
            return;
        }
        var name = (held.Data as PlacementItemTypeDef)?.DisplayName ?? "未知物品";
        _status.Text = Localizer.Tf("status.holding", name,
            held.Shape.Height, held.Shape.Width, held.Shape.CellCount);
    }

    /// <summary>读取 res://Data/Inventory/Items/ 下所有物品配置；目录缺失或为空时退回内置占位集。</summary>
    private static List<PlacementItemTypeDef> LoadItemDefs()
    {
        var list = new List<PlacementItemTypeDef>();
        if (DirAccess.DirExistsAbsolute("res://Data/Inventory/Items"))
        {
            var dir = DirAccess.Open("res://Data/Inventory/Items");
            if (dir != null)
            {
                foreach (var file in dir.GetFiles())
                {
                    if (!file.EndsWith(".tres")) continue;
                    var def = ResourceLoader.Load<PlacementItemTypeDef>("res://Data/Inventory/Items/" + file);
                    if (def != null) list.Add(def);
                }
            }
        }
        if (list.Count > 0) return list;
        list.AddRange(new[]
        {
            new PlacementItemTypeDef { DisplayName = "占位·单格", ShapePattern = new[] { "X" } },
            new PlacementItemTypeDef { DisplayName = "占位·三格条", ShapePattern = new[] { "XXX" } },
            new PlacementItemTypeDef { DisplayName = "占位·T 形", ShapePattern = new[] { "XXX", ".X." } },
        });
        return list;
    }
}
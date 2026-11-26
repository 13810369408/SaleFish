using System.Collections.Generic;
using Godot;
using SaleFish.Inventory;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 测试关卡：演示「局外配置驱动」的格子背包。
///  ▪ 物品类型 = res://Data/Inventory/Items/*.tres（PlacementItemTypeDef），加新配置即加新物品，无需改代码；
///     也可在检查器的 RandomPool 里自行指定随机池（指定后「生成物品」只从池中取，不再扫目录）；
///  ▪ 仓库 = 检查器里的 Storage（StorageTypeDef 资源），格大小（CellSize）与异形图案（ValidPattern）
///     都由该配置决定；缺省回退 res://Data/Inventory/Storage/MainBag.tres，仍无效则状态栏提示错误、不渲染网格。
///    ➜ 同一套物品配置换到任意格大小的仓库上自动适配。
///  ▪ 生成：空手时在鼠标上随机取一个已配置类型；丢弃：手持时移除。
///  ▪ 仓库生命周期参考：工具栏「创建仓库」= InventoryView.CreateStorage(parent, def, 位置)，
///     「销毁仓库」= InventoryView.RemoveStorage(view)（物品留手）；「隐藏/显示背包」= view.Hide()/Show()
///     （手持从它拿走的物品放回原位）。销毁后再创建返回同一实例即对象池复用。
/// 操作提示：左键 拿起/放下 · 右键 旋转 90° · 绿=可放 黄=可交换 红=冲突
/// </summary>
public sealed partial class TestSpatialInventory : Control
{
    /// <summary>仓库配置：在检查器里指定任意 StorageTypeDef 资源即决定仓库的形状（ValidPattern）与格大小（CellSize）。
    /// 为空时回退到 res://Data/Inventory/Storage/MainBag.tres；仍无效（缺文件 / 图案为空）时提示错误、不渲染网格。</summary>
    [Export] public StorageTypeDef? Storage = null;

    /// <summary>可选的随机池：在检查器里指定「生成物品」允许出现的类型；为空时回退到扫目录加载全部配置。</summary>
    [Export] public PlacementItemTypeDef[] RandomPool = [];

    /// <summary>运行期加载的配置物品列表（首个「生成」会用到）。</summary>
    private List<PlacementItemTypeDef> _defs = null!;

    private readonly RandomNumberGenerator _rng = new();

    private SpatialInventory _inventory = null!;
    private InventoryView _view = null!;
    private Label _status = null!;
    private StorageTypeDef? _storageDef;

    /// <summary>由「创建仓库」按钮生成的测试仓库（InventoryView.CreateStorage 返回值；销毁后置空）。</summary>
    private InventoryView? _created;

    /// <summary>上次销毁时归还的视图——再次创建若返回同一实例，即演示了对象池复用。</summary>
    private InventoryView? _lastDestroyed;

    public override void _Ready()
    {
        // 根节点必须放行鼠标事件，否则会吞掉点击，导致拿起/放下/旋转全部失效。
        MouseFilter = Control.MouseFilterEnum.Ignore;
        GetWindow().Size = new Vector2I(720, 760);
        _rng.Randomize();

        _defs = RandomPool.Length > 0 ? [.. RandomPool] : LoadItemDefs();

        // 数据层：仓库来自检查器配置的 StorageTypeDef（缺省回退 res://Data/Inventory/Storage/MainBag.tres）；
        // 图案无效（缺文件 / ValidPattern 为空）时按「不生成空间」处理——提示错误、不渲染网格。
        var storage = Storage ?? ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/MainBag.tres");
        var space = storage?.ToSpace();
        if (storage == null || space == null)
        {
            var why = Storage != null
                ? $"「{Storage.DisplayName}」的 ValidPattern 为空（未配置仓库图案）"
                : "未能加载默认仓库 res://Data/Inventory/Storage/MainBag.tres";
            GD.PrintErr($"[TestSpatialInventory] 仓库配置无效：{why}。请检查器里配置 Storage 资源。");
            BuildToolbar();
            _status.Text = $"仓库配置无效：{why}";
            return;
        }

        _storageDef = storage;
        _inventory = new SpatialInventory(space);

        // 视图层：输入由全局「手」（HeldItemController autoload）统一接管，无需每仓库控制器。
        _view = new InventoryView();
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);

        _view.Setup(_inventory, cellSize: storage.CellSize,
            gridOffset: new Vector2I(100, 92), storageLook: storage);

        HeldItemController.Instance.HeldChanged += OnHeldChanged;

        BuildToolbar();
        OnHeldChanged(null);
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
        // 回退：不依赖任何资源的占位定义，保证关卡离线也能跑
        list.AddRange(new[]
        {
            new PlacementItemTypeDef { DisplayName = "占位·单格", ShapePattern = new[] { "X" } },
            new PlacementItemTypeDef { DisplayName = "占位·三格条", ShapePattern = new[] { "XXX" } },
            new PlacementItemTypeDef { DisplayName = "占位·T 形", ShapePattern = new[] { "XXX", ".X." } },
        });
        return list;
    }

    private void BuildToolbar()
    {
        var bar = new HBoxContainer { Position = new Vector2(16, 18) };
        bar.AddThemeConstantOverride("separation", 10);
        AddChild(bar);

        var generateButton = new Button { Text = "生成物品" };
        // 手持时有物品时，点击此按钮不生效
        generateButton.Pressed += OnGeneratePressed;
        bar.AddChild(generateButton);

        var discardButton = new Button { Text = "丢弃" };
        discardButton.Pressed += () => HeldItemController.Instance?.DiscardHeld();   // autoload 全局手：丢弃当前手持
        bar.AddChild(discardButton);

        // 仓库生命周期参考代码：创建 = InventoryView.CreateStorage(parent, def, 位置)；销毁 = RemoveStorage(view)。
        var createButton = new Button { Text = "创建仓库" };
        createButton.Pressed += OnCreateStoragePressed;
        bar.AddChild(createButton);

        var destroyButton = new Button { Text = "销毁仓库" };
        destroyButton.Pressed += OnRemoveStoragePressed;
        bar.AddChild(destroyButton);

        // 背包隐藏开关：演示 InventoryView.Hide()/Show() 的「关闭背包」语义——
        // 隐藏时若手持从主背包拿走的物品会放回原位（连旋转复原）；销毁则物品留手。
        var hideButton = new Button { Text = "隐藏背包" };
        hideButton.Pressed += () => OnToggleBackpackPressed(hideButton);
        bar.AddChild(hideButton);

        _status = new Label
        {
            Position = new Vector2(16, 62),
            Text = "",
        };
        AddChild(_status);

        var help = new Label
        {
            Position = new Vector2(16, 720),
            Text = "左键 拿起/放下 · 右键 旋转 90° · 绿=可放 黄=可交换 红=冲突",
        };
        AddChild(help);
    }

    private void OnGeneratePressed()
    {
        // 已手持（生成在鼠标上）时按钮不生效；autoload 全局手始终存在
        if (HeldItemController.Instance.HeldItem != null) return;
        SpawnRandom();
    }

    /// <summary>「创建仓库」按钮：在固定位置用主仓库同款 Storage 配置再建一个完整仓库（示例调用，可反复参考）。</summary>
    private void OnCreateStoragePressed()
    {
        if (_storageDef == null)   // 配置无效路径：toolbar 已建但没有可用的仓库配置
        {
            _status.Text = "仓库配置无效，无法创建测试仓库";
            return;
        }
        if (_created != null)
        {
            _status.Text = "已有一个测试仓库：先点「销毁仓库」再重新创建";
            return;
        }

        _created = InventoryView.CreateStorage(this, _storageDef, new Vector2(100, 330));
        var reused = _created != null && ReferenceEquals(_created, _lastDestroyed);
        _status.Text = _created != null
            ? $"已创建测试仓库「{_storageDef.DisplayName}」（CreateStorage → {(reused ? "复用池中实例" : "新建实例")}，原点 (100,330)）"
            : "创建失败：仓储配置无效";
    }

    /// <summary>「销毁仓库」按钮：回收刚才创建的测试仓库（视图回对象池，同配置再创建会复用）。</summary>
    private void OnRemoveStoragePressed()
    {
        if (_created == null)
        {
            _status.Text = "没有可销毁的测试仓库：先点「创建仓库」";
            return;
        }

        _lastDestroyed = _created;
        InventoryView.RemoveStorage(_created);
        _created = null;
        _status.Text = $"已销毁测试仓库「{_storageDef?.DisplayName}」（RemoveStorage 已回收，可再次点「创建仓库」看复用效果）";
    }

    /// <summary>隐藏/显示主背包（演示两套生命周期里「隐藏」的语义；「销毁」见销毁按钮）。</summary>
    private void OnToggleBackpackPressed(Button button)
    {
        // 配置无效路径下没有主背包视图，按钮不应生效
        if (_view == null)
        {
            _status.Text = "没有可隐藏的主背包（仓库配置无效）";
            return;
        }

        if (_view.Visible)
        {
            _view.Hide();   // 手持从主背包拿走的物品会被放回原位（连旋转复原）
            button.Text = "显示背包";
            _status.Text = "已隐藏主背包：手持从它拿走的物品已放回原位（含旋转复原），且不再响应点击；再次点击按钮恢复";
        }
        else
        {
            _view.Show();
            button.Text = "隐藏背包";
            _status.Text = _storageDef != null
                ? $"已显示主背包「{_storageDef.DisplayName}」{_inventory.Size.X}×{_inventory.Size.Y}"
                : "已显示主背包";
        }
    }

    private void SpawnRandom()
    {
        var def = _defs[(int)_rng.RandiRange(0, _defs.Count - 1)];
        // 交给全局手，以主仓库为参考（锚点/格大小按主仓库格大小解析，随后可自由放入任意仓库）
        HeldItemController.Instance.TakeExternal(def.ToItem(), _view);
    }

    private void OnHeldChanged(InventoryItem? held)
    {
        if (held == null)
        {
            var s = _storageDef;
            _status.Text = s != null && _inventory != null
                ? $"空手 · 仓库「{s.DisplayName}」{_inventory.Size.X}×{_inventory.Size.Y}（左键点击物品可拿起）"
                : "空手（左键点击已放置的物品可拿起）";
            return;
        }
        var name = (held.Data as PlacementItemTypeDef)?.DisplayName ?? "未知物品";
        _status.Text = $"手持：{name}  {held.Shape.Height}×{held.Shape.Width} 占 {held.Shape.CellCount} 格";
    }
}
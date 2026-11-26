using System;
using System.Collections.Generic;
using Godot;
using SaleFish.Inventory;

namespace SaleFish.View;

/// <summary>
/// 仓库按钮组件（挂在场景里每个仓库开/关节点上，现为宝箱 Sprite2D「Inventory」）。
/// 把「打开/关闭某一座仓库」的职责收进节点自身，为多仓库铺路：每座仓库按钮各持一种 Storage 配置、
/// 各自维护自己的仓库面板（InventoryView，懒创建、Hide/Show 同一实例内容保留、Destroy 后重建归位），
/// 并记住打开位置——玩家拖拽过面板后，下次打开默认还在该位置。多仓库 = 场景多挂几个本组件、各配各的 Storage。
///
/// 分工（组件 vs 宿主）：
///  ▪ 组件管：Storage / 面板生命周期 / 打开位置（锚点推导 + 拖后记忆）/ 开-关-切换 三动作；
///  ▪ 宿主（如 MainStage）管：点击路由（_UnhandledInput 命中本组件所在精灵 → 调 Toggle）、
///    仓库模态规则（点击面板以外的行为）、状态栏（订阅 <see cref="Opened"/>/<see cref="Closed"/>）。
///  组件不碰宿主 UI，事件只负责「发生了什么」，展示由宿主处理。
///
/// 打开位置规则：首次打开用 <see cref="WarehouseAnchorPath"/> 锚点（_Ready 时相机休息位预计算成稳定兜底，
/// 未配/失效回退屏幕中心）；之后每次打开优先用上次记住的位置（含拖拽后的新位置），见 <see cref="Open"/>。
/// </summary>
public sealed partial class WarehouseButton : Sprite2D
{
    /// <summary>这座按钮开哪座仓库；为空回退 res://Data/Inventory/Storage/MainBag.tres。</summary>
    [Export] public StorageTypeDef? Storage;

    /// <summary>仓库面板（InventoryView）挂载的 UI 宿主（Control，如 UI 层的 Hud），相对本节点；两者缺一不可。</summary>
    [Export] public NodePath? UiHostPath;

    /// <summary>首次打开时的中心锚点（Node2D，可选）；未配置/节点无效回退屏幕中心。</summary>
    [Export] public NodePath? WarehouseAnchorPath;

    private StorageTypeDef? _storageDef;
    private Control? _uiHost;
    private Node2D? _anchor;
    private InventoryView? _view;

    /// <summary>下次打开的位置（UI 宿主本地坐标，面板左上角）；null = 尚未打开/未拖拽 → 用锚点推导。</summary>
    private Vector2? _lastOpenPos;

    /// <summary>锚点推导的「打开中心」兜底（UI 宿主本地坐标）：在 _Ready（相机休息位）预计算并缓存——
    /// 避免钓上自动开仓时相机正随鱼钩走远，把世界锚点经当前画布变换映射到屏幕外（仓库偏到最左边）。
    /// 拖拽/关闭后由 _lastOpenPos 接管，此值只在首次打开（无已记住位置）时兜底。</summary>
    private Vector2? _fallbackCenter;

    /// <summary>仓库当前是否打开（已创建且可见）。</summary>
    public bool IsOpen =>
        _view != null && GodotObject.IsInstanceValid(_view) && _view.Visible;

    /// <summary>本按钮当前维护的仓库视图（可空）。</summary>
    public InventoryView? View => _view;

    /// <summary>已生效的仓库配置（主状态栏显示名称用）。</summary>
    public StorageTypeDef? StorageDef => _storageDef;

    /// <summary>打开完成（创建或重新显示）时触发。</summary>
    public event Action? Opened;

    /// <summary>关闭完成时触发。</summary>
    public event Action? Closed;

    /// <summary>面板被拖拽到新位置（拖拽结束、位置有变化）时触发。</summary>
    public event Action? Moved;

    // ---- 静态命中登记表：多按钮并存时供宿主做「点击落在哪个按钮上」的判定 ----

    private static readonly List<WarehouseButton> _all = [];

    /// <summary>所有在用仓库按钮（供存档等系统枚举：快照/还原每一座仓库的面板与内容）。</summary>
    public static IReadOnlyList<WarehouseButton> All => _all;

    /// <summary>存档读档用：把仓库面板放到指定位置并记为「下次打开优先」的记忆位置（视图须已创建，Open 后再调）。</summary>
    public void RestorePosition(Vector2 uiLocalPos)
    {
        if (_view == null || !GodotObject.IsInstanceValid(_view)) return;
        _view.Position = uiLocalPos;
        _lastOpenPos = uiLocalPos;
    }

    private void Register()
    {
        if (!_all.Contains(this)) _all.Add(this);
    }

    private void Unregister()
    {
        _all.Remove(this);
    }

    /// <summary>给定画布坐标（世界/画布变换后的点）命中到的仓库按钮；未命中返回 null。
    /// 与宿主输入路由共用：命中 → 调该按钮的 Toggle（各开各的仓，模态规则不拦按钮自己的开关）。</summary>
    public static WarehouseButton? HitTest(Vector2 canvasPoint)
    {
        for (var i = _all.Count - 1; i >= 0; i--)
        {
            var b = _all[i];
            if (!GodotObject.IsInstanceValid(b)) { _all.RemoveAt(i); continue; }
            if (b.Texture == null) continue;
            if (b.GetRect().HasPoint(b.ToLocal(canvasPoint))) return b;
        }
        return null;
    }

    public override void _Ready()
    {
        Register();
        ResolveConfig();
        ComputeFallbackCenter();   // 相机休息位定锚点兜底位置（首次打开用它，不随镜头偏差）
        if (_uiHost == null)
            GD.PushWarning($"[WarehouseButton] {Name}：找不到仓库面板宿主（UiHostPath），仓库将无法打开。");
    }

    public override void _ExitTree()
    {
        Unregister();
        if (_view != null && GodotObject.IsInstanceValid(_view))
            _view.Moved -= OnViewMoved;
    }

    /// <summary>解析集中配置（导出字段 → 本地字段；可重复调用，运行期补配也有效）。</summary>
    private void ResolveConfig()
    {
        _storageDef ??= Storage;
        if (_storageDef == null)
            _storageDef = ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/MainBag.tres");
        _uiHost ??= UiHostPath != null ? GetNodeOrNull<Control>(UiHostPath) : null;
        _anchor ??= WarehouseAnchorPath != null ? GetNodeOrNull<Node2D>(WarehouseAnchorPath) : null;
    }

    /// <summary>打开这座仓库（仅打开、不切换）：已打开不变；创建过但隐藏中 = Show 同一实例、内容保留；
    /// 从未创建 = 经 InventoryView.CreateStorage 创建。位置 = 记忆位置（拖拽后）优先，否则锚点/屏幕中心。</summary>
    public void Open()
    {
        ResolveConfig();
        if (_storageDef == null)
        {
            GD.PushWarning($"[WarehouseButton] {Name}：仓库配置无效，无法打开。");
            return;
        }
        if (_uiHost == null)
        {
            GD.PushWarning($"[WarehouseButton] {Name}：面板宿主缺失（UiHostPath 无效），无法打开。");
            return;
        }
        if (IsOpen) return;   // 已打开：幂等，不做任何事

        if (_view == null || !GodotObject.IsInstanceValid(_view))
        {
            _view = InventoryView.CreateStorage(_uiHost, _storageDef, Vector2.Zero);
            if (_view == null)
            {
                // InventoryView.CreateStorage 已打印具体错误（ValidPattern 为空等）
                _view = null;
                GD.PushWarning($"[WarehouseButton] {Name}：仓库「{_storageDef.DisplayName}」创建失败（ValidPattern 为空？）。");
                return;
            }
            _view.OwnerButton = this;                 // 外部点击关仓据此找到「所属按钮」
            _view.Moved -= OnViewMoved;               // 防御：池化复用可能带旧订阅，先摘再加
            _view.Moved += OnViewMoved;
        }
        else
        {
            _view.Show();                             // 同一实例、内容保留
        }

        _view.ExpandToBackground(_storageDef);                // 交互区覆盖到背景盒（点背景也能拖/不误关仓）
        _view.Position = ResolveOpenPosition();
        _lastOpenPos = _view.Position;                // 记住（下次/重建优先用它）
        Opened?.Invoke();
    }

    /// <summary>关闭这座仓库：隐藏（内容保留、视图留在场上），记录当前位置供下次打开。</summary>
    public void Close()
    {
        if (_view == null || !GodotObject.IsInstanceValid(_view)) return;
        if (!_view.Visible) return;
        _lastOpenPos = _view.Position;
        _view.Hide();
        Closed?.Invoke();
    }

    /// <summary>切换开关状态（宿主命中本按钮时调用）。</summary>
    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>拖拽结束（面板位置有变化）→ 记下位置并通知宿主。</summary>
    private void OnViewMoved()
    {
        if (_view != null && GodotObject.IsInstanceValid(_view))
            _lastOpenPos = _view.Position;
        Moved?.Invoke();
    }

    /// <summary>本次打开位置：记忆位置（含拖拽结果）优先；没有则按锚点/屏幕中心推导。</summary>
    private Vector2 ResolveOpenPosition()
    {
        if (_lastOpenPos.HasValue) return _lastOpenPos.Value;
        if (_view == null) return Vector2.Zero;
        ComputeFallbackCenter();   // 兜底：_Ready 没算出来就现在算（_Ready 优先，保证相机在休息位）
        var center = _fallbackCenter ?? (_uiHost != null ? _uiHost.Size / 2f : Vector2.Zero);
        return center - _view.Size / 2f;
    }

    /// <summary>把锚点（世界 Marker2D）换算成 UI 宿主本地坐标的打开中心并缓存：世界坐标 →（视口画布变换）→
    /// 屏幕 →（宿主全局矩阵逆）→ 本地。在 _Ready（相机休息位）算一次，之后重复调用不重算——
    /// 保证「首次打开（含相机已随钩走远的钓上自动打开）也落在锚点对应的稳定屏幕位置上」。</summary>
    private void ComputeFallbackCenter()
    {
        if (_fallbackCenter.HasValue) return;
        if (_anchor == null || !GodotObject.IsInstanceValid(_anchor)) return;
        if (_uiHost == null) return;
        var screenPoint = GetViewport().GetCanvasTransform() * _anchor.GlobalPosition;
        _fallbackCenter = _uiHost.GetGlobalTransformWithCanvas().AffineInverse() * screenPoint;
    }
}
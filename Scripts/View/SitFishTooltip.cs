using Godot;
using SaleFish.Common;

namespace SaleFish.View;

/// <summary>
/// 「坐下钓鱼」悬浮提示（prefab：Scenes/SitFishTooltip.tscn，实例挂在 MainStage 的 UI/Hud 下）。
/// 一个纯提示控件：不吃鼠标（MouseFilter = Ignore），默认隐藏；由主关卡在
/// 「玩家在钓鱼点检测范围内 + 鼠标悬停在钓鱼点上」时调 <see cref="ShowAt"/> 显示，否则 <c>Hide()</c>。
///
/// 外观（字号/颜色/文案）全在 prefab 里改：文案用子节点 LocalizedText（key = ui.sit_fish）自动跟随语言；
/// 想微调"文字离光标多远"就改检查器里的 <see cref="CursorOffset"/>。
///
/// 定位：<b>左上角 = 鼠标处 + CursorOffset</b>。世界 → 视口用相机画布变换，再转到父控件本地坐标，
/// 因此相机移动/缩放时提示依然贴着光标（详见 Common.MouseWorld）。
/// </summary>
[GlobalClass]
public sealed partial class SitFishTooltip : Control
{
    /// <summary>相对鼠标位置的像素偏移（Control 本地像素）：默认 (0,0) = 文字框左上角正好在光标处。
    /// 想让字离光标远一点往右下挪，就填正值；往左上挪填负值。</summary>
    [Export] public Vector2 CursorOffset;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;   // 纯提示，不吞点击（鼠标点击要能穿到世界里的椅子）
        Visible = false;
    }

    /// <summary>在给定的<b>世界坐标</b>（通常是鼠标世界坐标）处显示本提示。</summary>
    public void ShowAt(Vector2 worldPosition)
    {
        var viewport = GetViewport();
        var screen = MouseWorld.ToScreen(viewport, worldPosition);   // 世界 → 视口（含相机）
        var parent = GetParent() as Control;                          // 父控件本地坐标（Hud 一般就是视口坐标）
        var local = parent != null
            ? parent.GetGlobalTransformWithCanvas().AffineInverse() * screen
            : screen;
        Position = local + CursorOffset;
        Visible = true;
    }
}

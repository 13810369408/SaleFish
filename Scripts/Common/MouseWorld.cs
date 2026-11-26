using Godot;

namespace SaleFish.Common;

/// <summary>
/// 鼠标「世界坐标」换算的唯一入口。
///
/// 坑（曾经踩过）：<c>Viewport.GetMousePosition()</c> 返回的是<b>视口/屏幕坐标</b>（左上角 = (0,0)，
/// 范围 0..1920 × 0..1080），<b>没有</b>经过 Camera2D 变换。把它直接当世界坐标用，只有在
/// 「相机恒在 (0,0) 且 zoom = 1」时才碰巧正确；主关卡相机静止时在 (0,0) 附近、FreeMove 跟随玩家、
/// 抛竿后跟随鱼钩，于是表现为「鼠标在屏幕左上角却被判定在椅子范围内」「悬浮提示跑到椅子上」。
///
/// 正确做法：视口鼠标坐标 × <c>GetCanvasTransform()</c> 的逆变换 → 世界坐标（相机位置/缩放/锚点全算进去）。
/// 等价于 <c>CanvasItem.GetGlobalMousePosition()</c>，但本类对任意 Node 都可用（autoload 不是 CanvasItem）。
/// </summary>
public static class MouseWorld
{
    /// <summary>当前鼠标的世界坐标（任意 Node 内调用；取该节点所在视口的相机变换）。</summary>
    public static Vector2 From(Node node)
    {
        var viewport = node.GetViewport();
        return viewport == null ? Vector2.Zero : FromScreen(viewport, viewport.GetMousePosition());
    }

    /// <summary>视口（屏幕）坐标 → 世界坐标（画布变换取逆）。测试可直接喂坐标，不必真的动鼠标。</summary>
    public static Vector2 FromScreen(Viewport viewport, Vector2 screenPosition) =>
        viewport.GetCanvasTransform().AffineInverse() * screenPosition;

    /// <summary>世界坐标 → 视口（屏幕）坐标（画布变换正变换）。UI 悬浮层定位用。</summary>
    public static Vector2 ToScreen(Viewport viewport, Vector2 worldPosition) =>
        viewport.GetCanvasTransform() * worldPosition;
}

using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 钓鱼点（场景 FishingSpot，Node2D）：一个可交互的「坐下钓鱼」落点。
/// 节点结构：Sprite（视觉，如椅子）+ Detect(Area2D > CollisionShape2D 矩形)【碰撞检测子节点】+ Highlight(Line2D 描边)。
/// 本组件只提供「几何判定 + 高亮显示切换」，不做输入 / 状态机 ——
/// 主关卡（MainStage 的 FreeMove 阶段）每帧问 IsPointInside(玩家 / 鼠标)、命中显示高亮与「坐下钓鱼」悬浮框，
/// 左键命中后让玩家以行走速度走到本点 X 再切回 Ready。
/// </summary>
[GlobalClass]
public sealed partial class FishingSpot : Node2D
{
    [Export] public NodePath? DetectPath = new("Detect");
    [Export] public NodePath? HighlightPath = new("Highlight");

    private Area2D? _detect;
    private Line2D? _highlight;

    public override void _Ready()
    {
        _detect = GetNodeOrNull<Area2D>(DetectPath);
        _highlight = GetNodeOrNull<Line2D>(HighlightPath);
    }

    /// <summary>检测范围（世界矩形）：从 Detect 子节点的未旋转矩形碰撞体推导（与 WaterBody.RectOf 同款规则）。</summary>
    public Rect2? WorldDetectRect
    {
        get
        {
            if (_detect == null) return null;
            if (_detect.GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return null;
            if (cs.Shape is not RectangleShape2D rect || cs.Rotation != 0f) return null;
            var size = rect.Size * cs.GlobalScale;
            return new Rect2(cs.GlobalPosition - size / 2f, size);
        }
    }

    /// <summary>给定世界坐标是否落在检测范围内。</summary>
    public bool IsPointInside(Vector2 worldPos) =>
        WorldDetectRect is { } r && r.HasPoint(worldPos);

    /// <summary>高亮描边开关（玩家进入检测范围时开，离开关）。</summary>
    public void SetHighlight(bool on)
    {
        if (_highlight != null && _highlight.Visible != on) _highlight.Visible = on;
    }
}
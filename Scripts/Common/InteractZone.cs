using Godot;

namespace SaleFish.Common;

/// <summary>
/// 世界里的「可交互区域」基类（Node2D）：一个视觉 Sprite + 一个矩形检测子节点 + 一条高亮描边。
/// 节点结构约定（子节点名可通过导出改）：
/// <code>
/// 本节点
/// ├── Sprite     视觉（椅子/鱼店/摊位…）
/// ├── Detect     Area2D &gt; CollisionShape2D（未旋转矩形）= 检测范围
/// └── Highlight  Line2D 高亮描边（默认隐藏）
/// </code>
/// 本基类只做两件事：**几何判定**（<see cref="WorldDetectRect"/> / <see cref="IsPointInside"/>）
/// 和**高亮开关**（<see cref="SetHighlight"/>）。输入路由与状态机留在宿主（如 MainStage）——
/// 钓鱼点、鱼店等都用它，避免每个交互点重复一份矩形推导代码。
/// </summary>
[GlobalClass]
public partial class InteractZone : Node2D
{
    /// <summary>矩形检测子节点（Area2D，其下应有未旋转的 CollisionShape2D + RectangleShape2D）。</summary>
    [Export] public NodePath? DetectPath = new("Detect");

    /// <summary>高亮描边节点（Line2D）。</summary>
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

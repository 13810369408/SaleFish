using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 水中参照小点（纯视觉装饰）：在水域内随机撒 Count 个半透明白点，让水面/水体有颗粒参照物，
/// 观察鱼游动、钩子下沉时更容易看出「水在往哪边流、动了多远」。挂在 Water 节点下：
/// Water 挂 WaterBody → 按湖床几何逐列采样（点不会落到水底以下/水面以上）；普通矩形 → 按矩形撒点。
/// 点随 Water 移动/旋转一起走。
///
/// 不参与任何逻辑（无碰撞、无处理循环）；_Ready 时布局一次，Seed 固定 → 每次进场景点的位置都一样
/// （便于当参照系；想换布局改 Seed 即可）。没有可用水域时警告并直接不画（空水域不报错）。
/// </summary>
public sealed partial class WaterDots : Node2D
{
    /// <summary>斑点数量（默认 120：水域 12000×1000 的尺度下一屏能看到一小簇，检查器可按观感调）。</summary>
    [Export] public int Count = 120;

    /// <summary>每个点半径（px）。</summary>
    [Export] public float Radius = 5f;

    /// <summary>点颜色（默认半透明白）。</summary>
    [Export] public Color DotsColor = new(1f, 1f, 1f, 0.7f);

    /// <summary>固定随机种子：同一种子布局稳定可复现；换一个数字就是一套新布局。</summary>
    [Export] public ulong Seed = 20261028;

    /// <summary>已生成的点（全局坐标，供 _Draw 与测试读）。</summary>
    public Vector2[] Points { get; private set; } = [];

    private Rect2? _rect;

    public override void _Ready()
    {
        Recompute();
    }

    /// <summary>重算斑点布局（改参数后调；供测试驱动）。</summary>
    public void Recompute()
    {
        var space = WaterBody.SpaceOf(GetParent() as Area2D);
        if (space != null)
        {
            Points = ScatterInWater(space);
            QueueRedraw();
            return;
        }

        _rect = WaterRectFromParent();
        if (_rect == null)
        {
            Points = [];
            GD.PushWarning("[WaterDots] 父节点没有可用的水域（未旋转的矩形碰撞体 / WaterBody），点布置失败：不画参照点。");
            QueueRedraw();
            return;
        }

        var rng = new RandomNumberGenerator { Seed = Seed };
        var rect = _rect.Value;
        var n = Mathf.Max(0, Count);

        // 点在矩形内随机撒，四周内缩 Radius 让圆不会被水边截断；矩形太小就退化成中心一个点
        var left = rect.Position.X + Radius;
        var right = rect.Position.X + rect.Size.X - Radius;
        var top = rect.Position.Y + Radius;
        var bottom = rect.Position.Y + rect.Size.Y - Radius;
        var spanX = Mathf.Max(right - left, 0f);
        var spanY = Mathf.Max(bottom - top, 0f);

        var pts = new Vector2[n];
        var center = rect.Position + rect.Size / 2f;
        for (int i = 0; i < n; i++)
        {
            pts[i] = new Vector2(
                spanX > 0f ? left + rng.RandfRange(0f, spanX) : center.X,
                spanY > 0f ? top + rng.RandfRange(0f, spanY) : center.Y);
        }
        Points = pts;
        QueueRedraw();
    }

    /// <summary>按水体几何采样：先取 x，再在该 x 的水柱里取 y（四周内缩 Radius）。</summary>
    private Vector2[] ScatterInWater(WaterSpace space)
    {
        var rng = new RandomNumberGenerator { Seed = Seed };
        var n = Mathf.Max(0, Count);

        var left = space.XMin + Radius;
        var right = space.XMax - Radius;
        var spanX = Mathf.Max(right - left, 0f);
        var centerX = (space.XMin + space.XMax) * 0.5f;

        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var x = spanX > 0f ? rng.RandfRange(left, right) : centerX;
            var top = space.SurfaceY + Radius;
            var bottom = space.FloorYAt(x) - Radius;
            var spanY = Mathf.Max(bottom - top, 0f);
            var y = spanY > 0f ? rng.RandfRange(top, bottom) : top;
            pts[i] = new Vector2(x, y);
        }
        return pts;
    }

    public override void _Draw()
    {
        var r = Mathf.Max(Radius, 0.5f);
        foreach (var p in Points)
            DrawCircle(ToLocal(p), r, DotsColor);
    }

    /// <summary>父节点矩形碰撞体包围盒（未旋转矩形；旋转/异形返回 null）。</summary>
    private Rect2? WaterRectFromParent()
    {
        if (GetParent() is not Node parent) return null;
        if (parent.GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return null;
        if (cs.Shape is not RectangleShape2D rectShape || cs.Rotation != 0f) return null;
        var size = rectShape.Size * cs.GlobalScale;
        return new Rect2(cs.GlobalPosition - size / 2f, size);
    }
}
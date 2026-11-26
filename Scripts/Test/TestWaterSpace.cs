using Godot;
using SaleFish.Fishing;

namespace SaleFish.Test;

/// <summary>
/// WaterSpace / WaterBody 测试（纯几何，不接鱼和钩子）：
///  ▪ 平水底：水柱、点是否在水里、四向距离；
///  ▪ 坡底 / 三点折线：FloorYAt 的二分与插值、越界水平延伸；
///  ▪ Probe：四个方向的边界、法线、maxDistance、clearance / surfaceMargin、水平游向抬高岸底；
///  ▪ WaterBody：无 Lakebed 回退平水底；有 Lakebed 时从多边形提取上包络（顺时针/逆时针点序都覆盖）。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestWaterSpace.tscn --quit-after 240
/// </summary>
public sealed partial class TestWaterSpace : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        TestFlatColumn();
        TestSlope();
        TestProbeFlat();
        TestProbeClearance();
        TestProbeRisingFloor();
        TestWaterBodyFallback();
        TestWaterBodyPolygon(clockwise: false);
        TestWaterBodyPolygon(clockwise: true);

        GD.Print($"[WaterSpaceTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[WaterSpaceTest] ALL PASSED" : "[WaterSpaceTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    // ---- 平水底：水柱 / 点判定 / 四向距离 ----

    private void TestFlatColumn()
    {
        var s = WaterSpace.Flat(0f, 1000f, 0f, 400f);   // 水面 y=0、水底 y=400、x ∈ [0, 1000)

        var c = s.ColumnAt(500f);
        Check("平底：x=500 有水", c.Has);
        Near("平底：水面", c.Top, 0f);
        Near("平底：水底", c.Bottom, 400f);
        Near("平底：深度", c.Depth, 400f);

        Check("平底：x=-1 无水", !s.ColumnAt(-1f).Has);
        Check("平底：x=1000 无水（半开区间）", !s.ColumnAt(1000f).Has);

        Check("平底：水里点", s.IsWater(new Vector2(500f, 100f)));
        Check("平底：水面上不算水", !s.IsWater(new Vector2(500f, 0f)));
        Check("平底：水底上不算水", !s.IsWater(new Vector2(500f, 400f)));
        Check("平底：水面上方不算水", !s.IsWater(new Vector2(500f, -10f)));

        var b = s.BoundsAt(new Vector2(500f, 150f));
        Near("平底：到左岸", b.ToLeft, 500f);
        Near("平底：到右岸", b.ToRight, 500f);
        Near("平底：到水面", b.ToSurface, 150f);
        Near("平底：到水底", b.ToBottom, 250f);
    }

    // ---- 坡底 / 折线：插值、越界延伸、列深 ----

    private void TestSlope()
    {
        var slope = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 100f), new Vector2(1000f, 300f)]);

        Near("坡底：x=0", slope.FloorYAt(0f), 100f);
        Near("坡底：x=250", slope.FloorYAt(250f), 150f);
        Near("坡底：x=500", slope.FloorYAt(500f), 200f);
        Near("坡底：x=1000", slope.FloorYAt(1000f), 300f);
        Near("坡底：x 越界左（水平延伸）", slope.FloorYAt(-50f), 100f);
        Near("坡底：x 越界右（水平延伸）", slope.FloorYAt(1500f), 300f);
        Near("坡底：x=500 深度", slope.ColumnAt(500f).Depth, 200f);
        Near("坡底：x=500 到水底", slope.BoundsAt(new Vector2(500f, 120f)).ToBottom, 80f);

        var bent = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 100f), new Vector2(400f, 300f), new Vector2(1000f, 50f)]);
        Near("三点折线：x=200（第一段）", bent.FloorYAt(200f), 200f);
        Near("三点折线：x=700（第二段）", bent.FloorYAt(700f), 175f);
        Near("三点折线：x=400（折点）", bent.FloorYAt(400f), 300f);

        // 同 x 的点会合并为较高（y 小）的那个
        var vertical = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 400f), new Vector2(500f, 400f), new Vector2(500f, 100f), new Vector2(1000f, 200f)]);
        Near("同 x 合并：x=500 取较高点", vertical.FloorYAt(500f), 100f);
        Check("同 x 合并：折线仍为 x 升序", IsAscending(vertical.Floor));
    }

    // ---- Probe：四向边界 + 法线 + maxDistance ----

    private void TestProbeFlat()
    {
        var s = WaterSpace.Flat(0f, 1000f, 0f, 400f);
        var p = new Vector2(500f, 100f);

        var down = s.Probe(p, Vector2.Down, 0f, 0f);
        CheckEdge("向下撞水底", down, WaterEdge.Bottom, 300f);
        Check("水底法线朝上（水内）", down.Normal.Y < -0.999f);

        var up = s.Probe(p, Vector2.Up, 0f, 0f);
        CheckEdge("向上撞水面", up, WaterEdge.Surface, 100f);
        Check("水面法线朝下（水内）", up.Normal.Y > 0.999f);

        var left = s.Probe(p, Vector2.Left, 0f, 0f);
        CheckEdge("向左撞左岸", left, WaterEdge.LeftShore, 500f);
        Check("左岸法线朝右（水内）", left.Normal.X > 0.999f);

        var right = s.Probe(p, Vector2.Right, 0f, 0f);
        CheckEdge("向右撞右岸", right, WaterEdge.RightShore, 500f);
        Check("右岸法线朝左（水内）", right.Normal.X < -0.999f);

        var diag = s.Probe(p, new Vector2(1f, 1f), 0f, 0f);
        CheckEdge("斜向下右：先撞水底", diag, WaterEdge.Bottom, 300f * Mathf.Sqrt(2f));

        var far = s.Probe(new Vector2(500f, 200f), Vector2.Down, 0f, 0f, 50f);
        Check("超出 maxDistance 不算命中", !far.Hit && Mathf.Abs(far.Distance - 50f) < 0.01f);
    }

    // ---- Probe：身体半径 / 水面余量 ----

    private void TestProbeClearance()
    {
        var s = WaterSpace.Flat(0f, 1000f, 0f, 400f);
        var p = new Vector2(500f, 100f);

        CheckEdge("水底留 10px", s.Probe(p, Vector2.Down, 10f, 0f), WaterEdge.Bottom, 290f);
        CheckEdge("水面余量 20px", s.Probe(p, Vector2.Up, 0f, 20f), WaterEdge.Surface, 80f);
        CheckEdge("水面余量至少等于身体半径", s.Probe(p, Vector2.Up, 10f, 0f), WaterEdge.Surface, 90f);
        CheckEdge("左岸留 10px", s.Probe(p, Vector2.Left, 10f, 0f), WaterEdge.LeftShore, 490f);

        var slope = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 100f), new Vector2(1000f, 300f)]);
        var q = new Vector2(500f, 100f);   // 正下方水底 y=200
        var normalDot = 1f / Mathf.Sqrt(1f + 0.04f);   // |dir·n|，坡度 0.2
        Near("坡底垂直留 10px（斜法线补偿）",
            slope.Probe(q, Vector2.Down, 10f, 0f).Distance, 100f - 10f / normalDot);
    }

    // ---- Probe：水平游向抬高的岸底也要撞到底 ----

    private void TestProbeRisingFloor()
    {
        // 水底 y = 300 − 0.2x：越往右越高，x=500 处抬到 y=200
        var s = new WaterSpace(0f, 1000f, 0f,
            [new Vector2(0f, 300f), new Vector2(1000f, 100f)]);

        var p = new Vector2(100f, 200f);
        var right = s.Probe(p, Vector2.Right, 0f, 0f);
        CheckEdge("水平游向抬高岸底：撞底", right, WaterEdge.Bottom, 400f);
        Check("抬高岸底法线指向水内（左上）", right.Normal.X < 0f && right.Normal.Y < 0f);

        CheckEdge("同一位置向下：撞底", s.Probe(p, Vector2.Down, 0f, 0f), WaterEdge.Bottom, 80f);
    }

    // ---- WaterBody：无 Lakebed → 平水底 ----

    private void TestWaterBodyFallback()
    {
        var water = MakeWater("WaterFlat", new Vector2(500f, 200f), new Vector2(1000f, 400f));

        Check("WaterBody：无 Lakebed 时水柱有效", water.Space.ColumnAt(500f).Has);
        Near("WaterBody：回退水面 = 矩形上边", water.Space.SurfaceY, 0f);
        Near("WaterBody：回退水底 = 矩形下边", water.Space.FloorYAt(500f), 400f);
        Near("WaterBody：左岸 = 矩形左边", water.Space.XMin, 0f);
        Near("WaterBody：右岸 = 矩形右边", water.Space.XMax, 1000f);
    }

    // ---- WaterBody：从 Lakebed 多边形提取上包络（两种点序） ----

    private void TestWaterBodyPolygon(bool clockwise)
    {
        var tag = clockwise ? "顺时针" : "逆时针";

        var poly = new CollisionPolygon2D { Name = "CollisionPolygon2D" };
        poly.Polygon = clockwise
            ?
            [
                new Vector2(0f, 100f), new Vector2(0f, 500f), new Vector2(1000f, 500f),
                new Vector2(1000f, 200f), new Vector2(500f, 50f),
            ]
            :
            [
                new Vector2(0f, 100f), new Vector2(500f, 50f), new Vector2(1000f, 200f),
                new Vector2(1000f, 500f), new Vector2(0f, 500f),
            ];

        var lakebed = new StaticBody2D { Name = $"Lakebed{tag}" };
        lakebed.AddChild(poly);
        AddChild(lakebed);

        var water = MakeWater($"Water{tag}", new Vector2(500f, 250f), new Vector2(1000f, 600f), poly);

        Check($"{tag}：上包络是 x 升序（{water.Space.Floor.Length} 个点）", IsAscending(water.Space.Floor));
        Near($"{tag}：x=0 水底", water.Space.FloorYAt(0f), 100f);
        Near($"{tag}：x=250 水底（插值）", water.Space.FloorYAt(250f), 75f);
        Near($"{tag}：x=500 水底（折点）", water.Space.FloorYAt(500f), 50f);
        Near($"{tag}：x=750 水底（插值）", water.Space.FloorYAt(750f), 125f);
        Near($"{tag}：x=1000 水底", water.Space.FloorYAt(1000f), 200f);

        var column = water.Space.ColumnAt(500f);
        Check($"{tag}：x=500 有水", column.Has);
        Near($"{tag}：x=500 水面 = 矩形上边", column.Top, -50f);
        Near($"{tag}：x=500 深度", column.Depth, 100f);

        // 水底折线必须上下包络：底边（y=500）不能被当成水底
        Check($"{tag}：没有把多边形底边当水底", water.Space.FloorYAt(500f) < 500f);
    }

    // ---- 构造辅助 ----

    private WaterBody MakeWater(string name, Vector2 position, Vector2 size, CollisionPolygon2D? lakebed = null)
    {
        var water = new WaterBody { Name = name, Position = position, Lakebed = lakebed, LakebedPath = new NodePath() };
        var cs = new CollisionShape2D
        {
            Name = "CollisionShape2D",
            Shape = new RectangleShape2D { Size = size },
        };
        water.AddChild(cs);
        AddChild(water);   // 触发 _Ready → Rebuild
        return water;
    }

    private static bool IsAscending(Vector2[] points)
    {
        for (var i = 1; i < points.Length; i++)
        {
            if (points[i].X <= points[i - 1].X) return false;
        }
        return true;
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    private void Near(string name, float actual, float expected, float eps = 0.01f)
        => Check($"{name}（实际 {actual:F3}，期望 {expected:F3}）", Mathf.Abs(actual - expected) <= eps);

    private void CheckEdge(string name, WaterProbe probe, WaterEdge edge, float distance, float eps = 0.01f)
        => Check($"{name}（边={probe.Edge} 距离={probe.Distance:F3} 期望={edge}/{distance:F3}）",
            probe.Hit && probe.Edge == edge && Mathf.Abs(probe.Distance - distance) <= eps);
}

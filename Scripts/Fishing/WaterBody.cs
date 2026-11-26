using System.Collections.Generic;
using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 挂在场景 Water（Area2D）上的水体脚本：启动时把「Water 的矩形碰撞体 + 可选的 Lakebed 碰撞多边形」
/// 烘焙成一个 <see cref="WaterSpace"/>（纯几何，之后逐帧查询不再碰节点/物理）。
///
/// 几何来源：
///  ▪ 左右岸 = Water 的未旋转 RectangleShape2D 的左右边（世界坐标）；
///  ▪ 水面   = 该矩形的上边；
///  ▪ 水底   = Lakebed 的 CollisionPolygon2D 的「上包络」（左岸到右岸沿水面一侧的那条链）；
///            没有 Lakebed（或提取失败）→ 回退为 Water 矩形的下边（平水底，与旧矩形行为一致）。
///
/// 上包络不依赖多边形点序（顺时针/逆时针都行）：先找最左/最右顶点，沿两条链各走到对面，
/// 取中点 x 处 y 更小（更高、更靠水面）的那条，最后统一成 x 升序。
///
/// 本脚本只提供查询，不移动任何东西。鱼/钩子接入是后续步骤。
/// </summary>
public sealed partial class WaterBody : Area2D
{
    /// <summary>水底来源（可选）：指向 Lakebed 下 CollisionPolygon2D 的路径。留空 = 平水底（Water 矩形底）。
    /// 用 NodePath 而不是节点引用：Water 在场景里通常排在 Lakebed 前面，节点引用在 _Ready 时可能还没解析。</summary>
    [Export] public NodePath LakebedPath = new("../Lakebed/CollisionPolygon2D");

    /// <summary>运行时的水底覆盖（程序化生成/测试用）；非空时优先于 <see cref="LakebedPath"/>。</summary>
    public CollisionPolygon2D? Lakebed { get; set; }

    /// <summary>烘焙好的水体几何。没找到有效水区时是 <see cref="WaterSpace.Empty"/>（任何 x 都无水）。
    /// 首次访问时若还没烘焙会自动构建（场景里 Water 的子节点可能先于 Water._Ready 访问它）。</summary>
    public WaterSpace Space
    {
        get
        {
            if (!_built) Rebuild();
            return _space ?? WaterSpace.Empty;
        }
    }

    private WaterSpace? _space;
    private bool _built;

    public override void _Ready() => Rebuild();

    /// <summary>
    /// 重新读取 Water 矩形与 Lakebed 多边形并烘焙。地形变化（程序化生成、编辑器里改形状）后调用。
    /// 程序化生成时也可以不挂 Lakebed，直接调用 <see cref="SetSpace"/> 塞入生成好的 WaterSpace。
    /// </summary>
    public void Rebuild()
    {
        _built = true;
        if (!TryGetWaterRect(out var xMin, out var xMax, out var surfaceY, out var bottomY))
        {
            _space = WaterSpace.Empty;
            GD.PushWarning("[WaterBody] 没找到 Water 下未旋转的 RectangleShape2D：水体检测不可用。");
            return;
        }

        Vector2[] flatFloor = [new Vector2(xMin, bottomY), new Vector2(xMax, bottomY)];

        var lakebed = ResolveLakebed();
        if (lakebed == null)
        {
            _space = new WaterSpace(xMin, xMax, surfaceY, flatFloor);   // 没有水底 → 平水底
            return;
        }

        var chain = ExtractSurfaceChain(WorldPolygon(lakebed));
        if (chain.Length < 2 || (chain.Length == 2 && Mathf.Abs(chain[0].X - chain[1].X) <= WaterSpace.Epsilon))
        {
            GD.PushWarning("[WaterBody] 无法从 Lakebed 多边形提取有效水底面（点不足 3 个或左右顶点重合）：回退为平水底。");
            _space = new WaterSpace(xMin, xMax, surfaceY, flatFloor);
            return;
        }

        _space = new WaterSpace(xMin, xMax, surfaceY, FitFloorToRange(chain, xMin, xMax));
    }

    /// <summary>直接替换水体几何（程序化生成水体时用）。</summary>
    public void SetSpace(WaterSpace space)
    {
        _space = space;
        _built = true;
    }

    // ---- 静态取用（Fish / Hook / FishSpawner / WaterDots 共用，避免各写一份 WaterRect） ----

    /// <summary>水区域未旋转 RectangleShape2D 的世界包围盒；找不到 / 旋转 / 非矩形 → null。</summary>
    public static Rect2? RectOf(Area2D? water)
    {
        if (water == null || !GodotObject.IsInstanceValid(water)) return null;
        if (water.GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return null;
        if (cs.Shape is not RectangleShape2D rectShape || cs.Rotation != 0f) return null;
        var size = rectShape.Size * cs.GlobalScale;
        return new Rect2(cs.GlobalPosition - size / 2f, size);
    }

    /// <summary>
    /// 取水体几何：Water 节点挂了 WaterBody → 用它烘焙好的 Space（含 Lakebed 湖床）；
    /// 普通 Area2D 矩形 → 等价的平水底水体（旧测试/旧场景不改也能工作）；没有可用水域 → null。
    /// </summary>
    public static WaterSpace? SpaceOf(Area2D? water)
    {
        if (water == null || !GodotObject.IsInstanceValid(water)) return null;
        if (water is WaterBody body) return body.Space;

        var rect = RectOf(water);
        if (rect == null || rect.Value.Size.X <= 0f || rect.Value.Size.Y <= 0f) return null;
        var r = rect.Value;
        return WaterSpace.Flat(r.Position.X, r.Position.X + r.Size.X, r.Position.Y, r.Position.Y + r.Size.Y);
    }

    /// <summary>取水底多边形：运行时覆盖优先，其次按 LakebedPath 解析；都没有 → null（平水底）。</summary>
    private CollisionPolygon2D? ResolveLakebed()
    {
        if (Lakebed != null && GodotObject.IsInstanceValid(Lakebed)) return Lakebed;
        if (string.IsNullOrEmpty(LakebedPath.ToString())) return null;
        return GetNodeOrNull(LakebedPath) as CollisionPolygon2D;
    }

    // ---- 烘焙：读取 Water 矩形 ----

    /// <summary>读取 Water 的未旋转矩形碰撞体（与旧 Fish/Hook.WaterRect 同一套几何规则）。</summary>
    private bool TryGetWaterRect(out float xMin, out float xMax, out float surfaceY, out float bottomY)
    {
        xMin = xMax = surfaceY = bottomY = 0f;
        if (GetNodeOrNull("CollisionShape2D") is not CollisionShape2D cs) return false;
        if (cs.Shape is not RectangleShape2D rectShape || cs.Rotation != 0f) return false;

        var size = rectShape.Size * cs.GlobalScale;
        if (size.X <= 0f || size.Y <= 0f) return false;

        var topLeft = cs.GlobalPosition - size / 2f;
        xMin = topLeft.X;
        xMax = topLeft.X + size.X;
        surfaceY = topLeft.Y;
        bottomY = topLeft.Y + size.Y;
        return true;
    }

    // ---- 烘焙：Lakebed 多边形 → 上包络（x 升序的水底线） ----

    /// <summary>把 CollisionPolygon2D 的局部顶点转换到世界坐标。</summary>
    private static Vector2[] WorldPolygon(CollisionPolygon2D polygon)
    {
        var local = polygon.Polygon;
        var world = new Vector2[local.Length];
        var gt = polygon.GlobalTransform;
        for (var i = 0; i < local.Length; i++) world[i] = gt * local[i];
        return world;
    }

    /// <summary>
    /// 从多边形顶点里提取「面向水面的上包络」：找最左/最右顶点，沿顺时针、逆时针两条链各走一遍，
    /// 取中点 x 处 y 更小（更高）的那条；结果统一为 x 升序。返回空数组 = 无法提取。
    /// </summary>
    private static Vector2[] ExtractSurfaceChain(Vector2[] points)
    {
        if (points == null || points.Length < 3) return [];

        var left = 0;
        var right = 0;
        for (var i = 1; i < points.Length; i++)
        {
            if (IsMoreLeft(points[i], points[left])) left = i;
            if (IsMoreRight(points[i], points[right])) right = i;
        }
        if (left == right) return [];

        var forward = Chain(points, left, right, +1);
        var backward = Chain(points, left, right, -1);

        var midX = (points[left].X + points[right].X) * 0.5f;
        var yForward = ChainYAt(forward, midX);
        var yBackward = ChainYAt(backward, midX);

        List<Vector2> top;
        if (float.IsPositiveInfinity(yForward)) top = backward;
        else if (float.IsPositiveInfinity(yBackward)) top = forward;
        else top = yForward <= yBackward ? forward : backward;

        if (top.Count >= 2 && top[0].X > top[^1].X) top.Reverse();
        return top.ToArray();
    }

    /// <summary>沿多边形索引方向逐点收集一条链（含两端）。</summary>
    private static List<Vector2> Chain(Vector2[] points, int from, int to, int step)
    {
        var list = new List<Vector2>();
        var n = points.Length;
        var i = from;
        while (true)
        {
            list.Add(points[i]);
            if (i == to) break;
            i = (i + step + n) % n;
        }
        return list;
    }

    /// <summary>链在某个 x 处的 y（跨越该 x 的所有线段取最小 y = 最高）；没有线段覆盖 → +∞。</summary>
    private static float ChainYAt(List<Vector2> chain, float x)
    {
        var best = float.PositiveInfinity;
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var a = chain[i];
            var b = chain[i + 1];
            var lo = Mathf.Min(a.X, b.X);
            var hi = Mathf.Max(a.X, b.X);
            if (x < lo - WaterSpace.Epsilon || x > hi + WaterSpace.Epsilon) continue;

            var dx = b.X - a.X;
            if (Mathf.Abs(dx) <= WaterSpace.Epsilon)
                best = Mathf.Min(best, Mathf.Min(a.Y, b.Y));
            else
                best = Mathf.Min(best, Mathf.Lerp(a.Y, b.Y, (x - a.X) / dx));
        }
        return best;
    }

    /// <summary>把水底链两端水平延伸到 Water 的左右岸（多边形没覆盖到岸边时补平）。</summary>
    private static Vector2[] FitFloorToRange(Vector2[] chain, float xMin, float xMax)
    {
        var list = new List<Vector2>(chain.Length + 2);
        if (chain[0].X > xMin + WaterSpace.Epsilon)
            list.Add(new Vector2(xMin, chain[0].Y));
        list.AddRange(chain);
        if (chain[^1].X < xMax - WaterSpace.Epsilon)
            list.Add(new Vector2(xMax, chain[^1].Y));
        return list.ToArray();
    }

    // 同 x 时取较高的点（水面一侧），保证竖直岸壁也能选到正确的角点
    private static bool IsMoreLeft(Vector2 p, Vector2 q)
    {
        if (p.X < q.X - WaterSpace.Epsilon) return true;
        if (p.X > q.X + WaterSpace.Epsilon) return false;
        return p.Y < q.Y;
    }

    private static bool IsMoreRight(Vector2 p, Vector2 q)
    {
        if (p.X > q.X + WaterSpace.Epsilon) return true;
        if (p.X < q.X - WaterSpace.Epsilon) return false;
        return p.Y < q.Y;
    }
}

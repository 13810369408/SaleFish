using System;
using System.Collections.Generic;
using Godot;

namespace SaleFish.Fishing;

/// <summary>沿某个方向探测时撞到的水面/岸/水底。</summary>
public enum WaterEdge
{
    /// <summary>没撞到任何边界（超出 maxDistance）。</summary>
    None,
    /// <summary>左岸（竖直墙）。</summary>
    LeftShore,
    /// <summary>右岸（竖直墙）。</summary>
    RightShore,
    /// <summary>水面（水平线）。</summary>
    Surface,
    /// <summary>水底（折线）。</summary>
    Bottom,
}

/// <summary>某一列（某个 x）的水体信息：有没有水、水面 y、水底 y。</summary>
public readonly struct WaterColumn
{
    /// <summary>这一列是否有水（x 在水体内，且水底低于水面）。</summary>
    public readonly bool Has;

    /// <summary>水面 y（世界坐标，向下为正）。</summary>
    public readonly float Top;

    /// <summary>水底 y（世界坐标，向下为正）。</summary>
    public readonly float Bottom;

    public WaterColumn(bool has, float top, float bottom)
    {
        Has = has;
        Top = top;
        Bottom = bottom;
    }

    /// <summary>没有水。</summary>
    public static WaterColumn None => new(false, 0f, 0f);

    /// <summary>水柱高度（px）。</summary>
    public float Depth => Has ? Bottom - Top : 0f;
}

/// <summary>从一个点到水体四面的直线距离（未扣身体半径）。</summary>
public readonly struct WaterBounds
{
    /// <summary>到左岸的 x 距离（px；点在左岸外为负）。</summary>
    public readonly float ToLeft;

    /// <summary>到右岸的 x 距离（px；点在右岸外为负）。</summary>
    public readonly float ToRight;

    /// <summary>到水面的 y 距离（px；点在水面上方为负）。</summary>
    public readonly float ToSurface;

    /// <summary>到当前 x 水底的 y 距离（px；点在水底下方为负）。</summary>
    public readonly float ToBottom;

    public WaterBounds(float toLeft, float toRight, float toSurface, float toBottom)
    {
        ToLeft = toLeft;
        ToRight = toRight;
        ToSurface = toSurface;
        ToBottom = toBottom;
    }
}

/// <summary>沿一个方向探测的结果：多远撞到哪一面、边界指向水内的法线。</summary>
public readonly struct WaterProbe
{
    /// <summary>是否在 maxDistance 内撞到边界。</summary>
    public readonly bool Hit;

    /// <summary>沿方向的可用距离（px；已扣身体半径与水面余量，未撞到时 = maxDistance）。</summary>
    public readonly float Distance;

    /// <summary>撞到的面。</summary>
    public readonly WaterEdge Edge;

    /// <summary>边界指向水内的单位法线（转向/滑动用）。</summary>
    public readonly Vector2 Normal;

    public WaterProbe(bool hit, float distance, WaterEdge edge, Vector2 normal)
    {
        Hit = hit;
        Distance = distance;
        Edge = edge;
        Normal = normal;
    }

    /// <summary>没撞到边界。</summary>
    public static WaterProbe None(float distance) => new(false, distance, WaterEdge.None, Vector2.Zero);
}

/// <summary>
/// 一个水体的几何查询（纯 C#，不继承 Node，不依赖物理引擎）：
/// 四面边界 = 左岸竖直墙 / 右岸竖直墙 / 水平水面 / 水底折线。
///
/// 三个查询（全部世界坐标、全部 O(log n) 或 O(log n + 少量线段)）：
///  ▪ ColumnAt(x)  —— 某个 x 的水柱：有没有水 / 水面 / 水底（生成、选巡逻目标用）；
///  ▪ BoundsAt(p)  —— 到四面的直线距离快照（贴边判断用）；
///  ▪ Probe(p, dir, clearance, surfaceMargin, maxDistance)
///                 —— 沿方向走多远会撞到哪一面（巡逻/追饵/挣扎的移动用）。
///
/// Probe 处理 clearance（身体半径）与 surfaceMargin（水面余量）：
///  ▪ 左右岸：边界向内缩 clearance；
///  ▪ 水面：下移 surfaceMargin（内部至少取 clearance，防止身体露在水面外）；
///  ▪ 水底：与折线求交后，再按 `clearance / |dir·n|` 退一段，保证鱼心离底至少 clearance。
///
/// 水底折线必须按 x 升序（构造时会排序、去重同 x 取较高点）。每个 x 只有一个水底（单值），
/// 不支持洞穴/悬挑。
/// </summary>
public sealed class WaterSpace
{
    /// <summary>几何比较容差（px / 单位向量分量）。</summary>
    public const float Epsilon = 1e-4f;

    /// <summary>左岸 x（世界坐标，水体区间为半开 [XMin, XMax)）。</summary>
    public float XMin { get; }

    /// <summary>右岸 x（世界坐标，水体区间为半开 [XMin, XMax)）。</summary>
    public float XMax { get; }

    /// <summary>水面 y（世界坐标，向下为正）。</summary>
    public float SurfaceY { get; }

    /// <summary>水底折线（世界坐标，x 严格升序）。</summary>
    public Vector2[] Floor { get; }

    /// <summary>
    /// 构造一个水体。floor 至少两个点；内部会按 x 排序，并把同 x 的点合并为较高（y 较小）的那个。
    /// 若 floor 没有覆盖 [xMin, xMax]，FloorYAt 会在两端做水平延伸。
    /// </summary>
    public WaterSpace(float xMin, float xMax, float surfaceY, Vector2[] floor)
    {
        if (floor == null || floor.Length < 2)
            throw new ArgumentException("水底折线至少需要两个点。", nameof(floor));

        var sorted = (Vector2[])floor.Clone();
        Array.Sort(sorted, static (a, b) => a.X.CompareTo(b.X));

        var merged = new List<Vector2>(sorted.Length);
        foreach (var p in sorted)
        {
            if (merged.Count > 0 && Mathf.Abs(merged[^1].X - p.X) <= Epsilon)
            {
                // 同一 x 的竖直线段：取较高的点（y 小）作为水面下的水底
                if (p.Y < merged[^1].Y) merged[^1] = p;
                continue;
            }
            merged.Add(p);
        }
        if (merged.Count < 2)
            throw new ArgumentException("水底折线在去重后不足两个点。", nameof(floor));

        Floor = merged.ToArray();
        XMin = xMin;
        XMax = xMax;
        SurfaceY = surfaceY;
    }

    /// <summary>平水底（矩形水体的等价物）：整个 [xMin, xMax] 的水底都是 floorY。</summary>
    public static WaterSpace Flat(float xMin, float xMax, float surfaceY, float floorY) =>
        new(xMin, xMax, surfaceY, [new Vector2(xMin, floorY), new Vector2(xMax, floorY)]);

    /// <summary>空水体（没有任何水）：作为「没找到有效水区」时的安全默认值。</summary>
    public static WaterSpace Empty =>
        new(0f, 0f, 0f, [new Vector2(0f, 0f), new Vector2(1f, 0f)]);

    /// <summary>x 是否在水体的水平范围内（半开区间 [XMin, XMax)）。</summary>
    public bool HasColumn(float x) => x >= XMin && x < XMax;

    /// <summary>
    /// 当前 x 的水底 y。x 在折线范围外时水平延伸两端（不会越界）。
    /// 二分定位所在线段后做精确线性插值。
    /// </summary>
    public float FloorYAt(float x)
    {
        if (x <= Floor[0].X) return Floor[0].Y;
        if (x >= Floor[^1].X) return Floor[^1].Y;

        var i = FindSegmentIndex(x);
        var a = Floor[i];
        var b = Floor[i + 1];
        var dx = b.X - a.X;
        if (dx <= Epsilon) return Mathf.Min(a.Y, b.Y);
        return Mathf.Lerp(a.Y, b.Y, (x - a.X) / dx);
    }

    /// <summary>
    /// x 范围内水底的最低点（y 最小 = 最浅处）。巡逻矩形用它保证「整段左右路径都在水内」：
    /// 巡逻带的下界不能低于这个值。
    /// </summary>
    public float FloorMinY(float xMin, float xMax)
    {
        if (xMin > xMax) (xMin, xMax) = (xMax, xMin);

        var best = Mathf.Min(FloorYAt(xMin), FloorYAt(xMax));
        var i0 = FindSegmentIndex(xMin);
        var i1 = FindSegmentIndex(xMax);
        for (var i = i0; i <= i1; i++) best = Mathf.Min(best, Floor[i].Y);
        return best;
    }

    /// <summary>某个 x 的水柱：不在水平范围内、或水底不高于水面 → Has = false。</summary>
    public WaterColumn ColumnAt(float x)
    {
        if (!HasColumn(x)) return WaterColumn.None;
        var bottom = FloorYAt(x);
        return bottom > SurfaceY
            ? new WaterColumn(true, SurfaceY, bottom)
            : WaterColumn.None;
    }

    /// <summary>点是否在水里（严格内部：正好在水面/水底上的点不算）。</summary>
    public bool IsWater(Vector2 p)
    {
        var c = ColumnAt(p.X);
        return c.Has && p.Y > c.Top && p.Y < c.Bottom;
    }

    /// <summary>点 p 到水体四面的直线距离（不扣身体半径）。</summary>
    public WaterBounds BoundsAt(Vector2 p) => new(
        p.X - XMin,
        XMax - p.X,
        p.Y - SurfaceY,
        FloorYAt(p.X) - p.Y);

    /// <summary>
    /// 把一次位移 offset（世界向量）限制在水内，返回实际可走的位移：
    /// 无阻挡 = offset 原样；撞到边界 = 先走到边界，再沿边界切线滑掉剩余长度（水面/水底横向滑，岸壁竖向滑）。
    /// 鱼巡逻/追饵与钩子被挣扎拖走都用它，保证不会顶着头陷进岸边/水底/水面。
    /// </summary>
    public Vector2 MoveInside(Vector2 from, Vector2 offset, float clearance = 0f, float surfaceMargin = 0f)
    {
        var step = offset.Length();
        if (step <= Epsilon) return Vector2.Zero;
        var dir = offset / step;

        var probe = Probe(from, dir, clearance, surfaceMargin, step);
        if (!probe.Hit) return offset;

        var moved = Mathf.Clamp(probe.Distance, 0f, step);
        var pos = from + dir * moved;
        var remaining = step - moved;
        if (remaining <= Epsilon) return pos - from;

        var tangent = dir - probe.Normal * dir.Dot(probe.Normal);
        if (tangent.LengthSquared() <= 1e-8f) return pos - from;
        tangent = tangent.Normalized();

        var slide = Probe(pos, tangent, clearance, surfaceMargin, remaining);
        var slideDist = slide.Hit ? Mathf.Clamp(slide.Distance, 0f, remaining) : remaining;
        return pos + tangent * slideDist - from;
    }

    /// <summary>
    /// 从 p 沿 dir 探测最近的边界（已扣 clearance / surfaceMargin）。
    /// 左右岸、水面是精确求交；水底对折线逐段求交，并按 `clearance / |dir·n|` 退让。
    /// 没撞到（距离超过 maxDistance）返回 Hit = false、Distance = maxDistance。
    /// </summary>
    public WaterProbe Probe(Vector2 p, Vector2 dir, float clearance, float surfaceMargin,
        float maxDistance = float.PositiveInfinity)
    {
        clearance = Mathf.Max(0f, clearance);
        surfaceMargin = Mathf.Max(clearance, surfaceMargin);   // 水面余量至少盖住身体半径

        if (maxDistance <= 0f) return WaterProbe.None(0f);
        var lenSq = dir.LengthSquared();
        if (lenSq < 1e-12f) return WaterProbe.None(maxDistance);

        var d = dir / Mathf.Sqrt(lenSq);
        var bestT = maxDistance;
        var edge = WaterEdge.None;
        var normal = Vector2.Zero;

        // 左岸（竖直墙）
        if (d.X < -Epsilon)
        {
            var t = (p.X - (XMin + clearance)) / -d.X;
            if (t < 0f) t = 0f;
            if (t < bestT) { bestT = t; edge = WaterEdge.LeftShore; normal = Vector2.Right; }
        }

        // 右岸（竖直墙）
        if (d.X > Epsilon)
        {
            var t = ((XMax - clearance) - p.X) / d.X;
            if (t < 0f) t = 0f;
            if (t < bestT) { bestT = t; edge = WaterEdge.RightShore; normal = Vector2.Left; }
        }

        // 水面（水平线）
        if (d.Y < -Epsilon)
        {
            var t = (p.Y - (SurfaceY + surfaceMargin)) / -d.Y;
            if (t < 0f) t = 0f;
            if (t < bestT) { bestT = t; edge = WaterEdge.Surface; normal = Vector2.Down; }
        }

        // 水底（折线）：无论方向都测——水平游向抬高的岸边时也会撞到底
        if (TryProbeFloor(p, d, clearance, bestT, out var floorT, out var floorNormal) && floorT < bestT)
        {
            bestT = floorT;
            edge = WaterEdge.Bottom;
            normal = floorNormal;
        }

        return edge == WaterEdge.None ? WaterProbe.None(maxDistance) : new WaterProbe(true, bestT, edge, normal);
    }

    /// <summary>射线与水底折线求交（取最近、且正在接近的那一段），返回扣掉 clearance 后的距离。</summary>
    private bool TryProbeFloor(Vector2 p, Vector2 d, float clearance, float maxT,
        out float hitDistance, out Vector2 hitNormal)
    {
        hitDistance = float.PositiveInfinity;
        hitNormal = Vector2.Zero;

        // 把探测方向在当前水体范围内截断，避免 maxT = ∞ 时算出 NaN / 扫整条折线
        var cap = float.IsInfinity(maxT) ? (XMax - XMin) * 2f + 1f : maxT;
        var xEnd = p.X + d.X * cap;
        var xLo = Mathf.Clamp(Mathf.Min(p.X, xEnd), XMin, XMax);
        var xHi = Mathf.Clamp(Mathf.Max(p.X, xEnd), XMin, XMax);
        if (Mathf.Abs(d.X) <= Epsilon) xLo = xHi = Mathf.Clamp(p.X, XMin, XMax);

        var i0 = FindSegmentIndex(xLo);
        var i1 = FindSegmentIndex(xHi);

        for (var i = i0; i <= i1 && i < Floor.Length - 1; i++)
        {
            var a = Floor[i];
            var b = Floor[i + 1];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            if (Mathf.Abs(ex) <= Epsilon) continue;   // 竖直段不参与水底求交（已去重，正常不会出现）

            // 指向水内的法线（x 升序折线的上侧）
            var n = new Vector2(ey, -ex).Normalized();
            var approach = d.X * n.X + d.Y * n.Y;     // dir·n；< 0 表示正在接近水底
            if (approach >= -Epsilon) continue;

            var denom = d.X * ey - d.Y * ex;
            if (Mathf.Abs(denom) <= Epsilon) continue;   // 平行

            var qpx = a.X - p.X;
            var qpy = a.Y - p.Y;
            var t = (qpx * ey - qpy * ex) / denom;       // 射线参数
            var u = (qpx * d.Y - qpy * d.X) / denom;     // 线段参数
            if (t < 0f || u < -Epsilon || u > 1f + Epsilon) continue;

            // 垂直留 clearance：射线方向与法线夹角越斜，沿射线要多退一些
            var t2 = t - clearance / Mathf.Max(-approach, 1e-4f);
            if (t2 < 0f) t2 = 0f;
            if (t2 < hitDistance)
            {
                hitDistance = t2;
                hitNormal = n;
            }
        }

        return hitDistance < float.PositiveInfinity;
    }

    /// <summary>找 x 所在的水底线段下标（返回 i，使 Floor[i].X &lt;= x &lt;= Floor[i+1].X；越界时夹到端点段）。</summary>
    private int FindSegmentIndex(float x)
    {
        if (x <= Floor[0].X) return 0;
        if (x >= Floor[^1].X) return Floor.Length - 2;

        var lo = 0;
        var hi = Floor.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (Floor[mid].X <= x) lo = mid;
            else hi = mid;
        }
        return lo;
    }
}

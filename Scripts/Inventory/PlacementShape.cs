using System;
using System.Collections.Generic;
using Godot;

namespace SaleFish.Inventory;

/// <summary>
/// 放置物形状：以「布尔格子网格」描述的纯数学结构，与图片、碰撞体完全无关。
/// 形状通过 ASCII 图案（X=占用，.=空白）或代码构造，天然可编辑、可扩展。
/// </summary>
/// <example>
/// 3×1 长条：      { "XXX" }
/// L 形（拐棍）：  { "X.", "XX" }
/// T 形：          { "XXX", ".X." }
/// </example>
public sealed class PlacementShape
{
    public readonly int Width;    // 列数
    public readonly int Height;   // 行数
    private readonly bool[] _cells;

    private int? _cellCount;

    public PlacementShape(int width, int height, bool[] cells)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("形状宽高必须为正整数");
        if (cells.Length != width * height)
            throw new ArgumentException("格子数组长度必须等于 宽*高");
        Width = width;
        Height = height;
        _cells = cells;
    }

    /// <summary>形状的包围盒尺寸。</summary>
    public Vector2I Size => new(Width, Height);

    /// <summary>该形状占用的格子数。</summary>
    public int CellCount
    {
        get
        {
            _cellCount ??= CountOccupied();
            return _cellCount.Value;
        }
    }

    private int CountOccupied()
    {
        var n = 0;
        for (var i = 0; i < _cells.Length; i++)
            if (_cells[i]) n++;
        return n;
    }

    public bool IsCell(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && _cells[y * Width + x];

    public bool IsCell(Vector2I c) => IsCell(c.X, c.Y);

    // ---- 抓取/旋转锚点（拿起时的吸附候选，格内 0..1 比例坐标，不依赖具体 CellSize） ----

    /// <summary>
    /// cell 格可作抓取锚点的位置。过滤规则：
    ///  ▪ 中心始终可用；
    ///  ▪ 边中点仅在该边为形状内两个格子共用边时可用；
    ///  ▪ 角仅在被 ≥3 个格子（含本格）共用时可用。
    /// 格内坐标以左上角为 (0,0)，右下角为 (1,1)，数值即比例。
    /// </summary>
    public Vector2[] GrabAnchorFractions(Vector2I cell)
    {
        var (cx, cy) = (cell.X, cell.Y);
        var anchors = new List<Vector2>(9);

        // 中心：始终可用
        anchors.Add(new Vector2(0.5f, 0.5f));

        // 边中点：该边需为形状内两格共用的边
        if (IsCell(cx, cy - 1)) anchors.Add(new Vector2(0.5f, 0f));   // 上
        if (IsCell(cx, cy + 1)) anchors.Add(new Vector2(0.5f, 1f));   // 下
        if (IsCell(cx - 1, cy)) anchors.Add(new Vector2(0f, 0.5f));   // 左
        if (IsCell(cx + 1, cy)) anchors.Add(new Vector2(1f, 0.5f));   // 右

        // 角：被 ≥3 个格子（含本格）共用
        if (CornerSharedCount(cx, cy) >= 3) anchors.Add(new Vector2(0f, 0f));        // 左上
        if (CornerSharedCount(cx + 1, cy) >= 3) anchors.Add(new Vector2(1f, 0f));    // 右上
        if (CornerSharedCount(cx, cy + 1) >= 3) anchors.Add(new Vector2(0f, 1f));    // 左下
        if (CornerSharedCount(cx + 1, cy + 1) >= 3) anchors.Add(new Vector2(1f, 1f)); // 右下

        return anchors.ToArray();
    }

    /// <summary>lattice 顶点 (vx,vy) 周围 4 个格子中属于本形状的数量（该角被几个格子共用）。</summary>
    private int CornerSharedCount(int vx, int vy)
    {
        var n = 0;
        for (var dy = -1; dy <= 0; dy++)
        for (var dx = -1; dx <= 0; dx++)
            if (IsCell(vx + dx, vy + dy)) n++;
        return n;
    }

    /// <summary>形状内部的相对坐标（以左上角为 (0,0)）下所有占用格。</summary>
    public IEnumerable<Vector2I> GetOccupiedCells()
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (IsCell(x, y))
                yield return new Vector2I(x, y);
    }

    /// <summary>顺时针旋转 90°。新的宽高互换，占用格坐标随之变换。</summary>
    public PlacementShape RotatedClockwise()
    {
        var rotated = new bool[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (IsCell(x, y))
                rotated[(Height - 1 - y) + x * Height] = true;
        return new PlacementShape(Height, Width, rotated);
    }

    /// <summary>
    /// 用 ASCII 图案构造形状：'X'/'x'/'#' 表示占用，其余为空白。
    /// 每行宽度可以不一致：以最长行定义宽度，短行欠缺的部分自动按空位（未被占用）填充。
    /// 例如 { "XX", "X" } 等价于 2×2 的 { "XX", "X." }。
    /// </summary>
    public static PlacementShape FromPattern(params string[] rows)
    {
        if (rows.Length == 0)
            throw new ArgumentException("至少需要一行");
        var w = 0;
        foreach (var row in rows)
            w = Math.Max(w, row.Length);
        if (w <= 0)
            throw new ArgumentException("图案宽度必须大于 0");
        var h = rows.Length;
        var cells = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = rows[y];
            for (var x = 0; x < row.Length; x++)
            {
                var ch = row[x];
                cells[y * w + x] = ch == 'X' || ch == 'x' || ch == '#';
            }
            // 行内欠缺的部分保持 false（自动空位填充）
        }
        return new PlacementShape(w, h, cells);
    }

    /// <summary>实心矩形，w×h。</summary>
    public static PlacementShape Rectangle(int w, int h)
    {
        var cells = new bool[w * h];
        Array.Fill(cells, true);
        return new PlacementShape(w, h, cells);
    }

    /// <summary>直线，长度为 Length 的 1×Length 形状。</summary>
    public static PlacementShape Line(int length) => Rectangle(length, 1);

    /// <summary>L 形 / 拐棍：2×2 缺一角。</summary>
    public static PlacementShape LShape() => FromPattern("X.", "XX");

    /// <summary>T 形：3 宽 2 高。</summary>
    public static PlacementShape TShape() => FromPattern("XXX", ".X.");

    /// <summary>Z 形（S 形变体）。</summary>
    public static PlacementShape ZShape() => FromPattern(".XX", "XX.");

    /// <summary>导出回 ASCII 图案，便于调试与展示。</summary>
    public string[] ToPattern()
    {
        var rows = new string[Height];
        for (var y = 0; y < Height; y++)
        {
            var chars = new char[Width];
            for (var x = 0; x < Width; x++)
                chars[x] = IsCell(x, y) ? 'X' : '.';
            rows[y] = new string(chars);
        }
        return rows;
    }

    public override string ToString() => string.Join("\n", ToPattern());
}
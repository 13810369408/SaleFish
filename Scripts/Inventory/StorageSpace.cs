using System;
using Godot;

namespace SaleFish.Inventory;

/// <summary>
/// 储存空间：一张网格 + 每格「可用/不可用」声明。
/// 方形 = 全部可用；带凸起/凹陷的特殊图案 = 把特定格子标为不可用即可，天然形成异形。
/// 空间本身不关心放的是什么，只声明「哪些格子允许被占用」。
/// </summary>
public sealed class StorageSpace
{
    public readonly Vector2I Size;
    private readonly bool[] _validCells;

    public StorageSpace(Vector2I size, bool[] validCells)
    {
        if (size.X <= 0 || size.Y <= 0)
            throw new ArgumentException("空间尺寸必须为正");
        if (validCells.Length != size.X * size.Y)
            throw new ArgumentException("有效性数组长度必须等于 宽*高");
        Size = size;
        _validCells = validCells;
    }

    /// <summary>是否位于空间范围内。</summary>
    public bool IsInside(Vector2I c) =>
        c.X >= 0 && c.Y >= 0 && c.X < Size.X && c.Y < Size.Y;

    /// <summary>该格是否可被占用（必须同时位于范围内且被声明为可用）。</summary>
    public bool IsValid(Vector2I c) => IsInside(c) && _validCells[c.Y * Size.X + c.X];

    /// <summary>程序化修改某格可用性（用于代码动态生成异形空间）。</summary>
    public void SetValid(Vector2I c, bool valid)
    {
        if (!IsInside(c)) return;
        _validCells[c.Y * Size.X + c.X] = valid;
    }

    /// <summary>方形空间：全部格子可用。</summary>
    public static StorageSpace Rectangle(Vector2I size)
    {
        var cells = new bool[size.X * size.Y];
        Array.Fill(cells, true);
        return new StorageSpace(size, cells);
    }

    /// <summary>
    /// 用 ASCII 图案构造空间：'#' / 'X' 表示可用，其余（如 '.'、' '）表示不可用。
    /// 每行宽度可以不一致：以最长行定义宽度，短行欠缺的部分自动按不可用填充。
    /// 例（8×8 带一个凹陷和一根凸柱）：
    ///   { "########",
    ///     "########",
    ///     "##....##",
    ///     "##....##",
    ///     "####.####",
    ///     "########",
    ///     "########",
    ///     "########" }
    /// </summary>
    public static StorageSpace FromPattern(params string[] rows)
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
                cells[y * w + x] = ch == '#' || ch == 'X' || ch == 'x';
            }
            // 行内欠缺的部分保持 false（自动按不可用填充）
        }
        return new StorageSpace(new Vector2I(w, h), cells);
    }
}
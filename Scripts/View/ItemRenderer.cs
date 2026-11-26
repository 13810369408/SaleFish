using System.Collections.Generic;
using Godot;
using SaleFish.Common;
using SaleFish.Inventory;

namespace SaleFish.View;

/// <summary>
/// 物品渲染共享静态库：所有「按格绘制物品 / 整图绘制」的底层逻辑都在这里，仓库视图与全局手持渲染层
/// （HandRenderLayer）共用，避免同一套绘制在两处各自实现。坐标由调用方给定（视图用视图本地、手持层用
/// 全局），格大小也由参数传入——静态函数没有自己的 CellSize。
/// </summary>
public static class ItemRenderer
{
    /// <summary>顺时针（y 向下，与 DrawSetTransform 正向一致）把向量旋转给定弧度。</summary>
    public static Vector2 RotateVector(Vector2 v, float radians)
    {
        var c = Mathf.Cos(radians);
        var s = Mathf.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>贴图在当前朝向下的包围盒尺寸（像素）：显示尺寸 = DisplaySize × 格大小（DisplaySize 单位=格）。
    /// 显式配置的 DisplaySize 表示「物品自身(未旋转)朝向」的格数——旋转（turns 奇数次）时宽高互换；
    /// 未配置（或某轴 0）用该轴<b>形状</b>格数（shape 已含当前朝向，不再额外旋转）。
    /// def 为 null（占位色块路径）时按格尺寸计算。</summary>
    public static Vector2 SpriteCurrentSize(PlacementShape shape, PlacementItemTypeDef? def, int cellSize, int turns = 0)
    {
        var cell = (float)cellSize;
        var size = def?.DisplaySize ?? Vector2.Zero;
        if (size.X > 0f && size.Y > 0f)
        {
            var dx = size.X;
            var dy = size.Y;
            if ((turns & 1) != 0) (dx, dy) = (dy, dx);   // DisplaySize 随旋转交换宽高（如 3×2 → 2×3）
            return new Vector2(dx, dy) * cell;
        }
        return new Vector2(
            (size.X > 0f ? size.X : shape.Width) * cell,
            (size.Y > 0f ? size.Y : shape.Height) * cell);
    }

    /// <summary>贴图内容的基础尺寸：朝向了 turns 次 90°（奇数次时宽高互换），即「未旋转原图」铺满时的像素尺寸。</summary>
    public static Vector2 SpriteBaseSize(Vector2 currentSize, int turns)
        => (turns & 1) != 0 ? new Vector2(currentSize.Y, currentSize.X) : currentSize;

    /// <summary>物品是否配置了整张贴图（贴图一律来自配置，代码不内置任何贴图）。</summary>
    public static bool TryGetDefTexture(InventoryItem item, out Texture2D texture, out PlacementItemTypeDef def)
    {
        if (item.Data is PlacementItemTypeDef d && d.Texture != null)
        {
            texture = d.Texture;
            def = d;
            return true;
        }
        texture = null!;
        def = null!;
        return false;
    }

    /// <summary>无贴图物品的渲染底色：统一白色占位（有贴图的物品整图原样显示，不经此逻辑）。</summary>
    public static readonly Color FallbackCellColor = Colors.White;

    /// <summary>
    /// 整图绘制：把贴图绕 center 旋转 totalRad 弧度后绘制到 localOrigin 起的 baseSize 矩形。
    /// 旋转的是「贴图内容」——物品旋转时图片跟着转，而不是被重新拉伸：
    /// baseSize 始终用未旋转前的格子宽高，旋转后正好贴合当前包围盒。
    /// localOrigin = -baseSize/2 + DrawOffset×格大小（偏移在贴图自身坐标里，随内容一起旋转）。
    /// </summary>
    public static void DrawItemSpriteRotated(CanvasItem canvas, Texture2D texture, Vector2 center,
        Vector2 baseSize, Vector2 localOrigin, float totalRad, Color modulate)
    {
        canvas.DrawSetTransform(center, totalRad, Vector2.One);
        canvas.DrawTextureRect(texture, new Rect2(localOrigin, baseSize), false, modulate);
        canvas.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// <summary>按形状逐格绘制一个物品（局部缩进，颜色可带透明度，可自定义描边色）。</summary>
    public static void DrawItemCells(CanvasItem canvas, PlacementShape shape, Vector2 originPx,
        Color color, float alpha, int cellSize, Color? borderColor = null)
    {
        var cell = (float)cellSize;
        const float inset = 4f;
        var size = new Vector2(cell, cell) - Vector2.One * inset * 2f;

        foreach (var c in shape.GetOccupiedCells())
        {
            var rect = new Rect2(
                originPx + new Vector2(c.X * cell, c.Y * cell) + Vector2.One * inset, size);
            var fill = color;
            fill.A = alpha;
            var border = borderColor ?? color.Darkened(0.35f);
            border.A = alpha;
            canvas.DrawRect(rect, fill, true);
            canvas.DrawRect(rect, border, false, 1.5f);
        }
    }

    /// <summary>手持物品底下的简单投影，让浮在空中的物品有「被提起」的体量感。</summary>
    public static void DrawItemShadow(CanvasItem canvas, PlacementShape shape, Vector2 originPx,
        float alpha, int cellSize)
    {
        var cell = (float)cellSize;
        const float inset = 6f;
        var size = new Vector2(cell, cell) - Vector2.One * inset * 2f;
        var shadow = new Color(0f, 0f, 0f, alpha);
        foreach (var c in shape.GetOccupiedCells())
            canvas.DrawRect(new Rect2(originPx + new Vector2(c.X * cell, c.Y * cell) + new Vector2(3f, 3f), size),
                shadow, true);
    }

    /// <summary>旋转动画中的手持本体：围绕 pivot（滑动的盒中心）旋转 rot 弧度绘制 shape 的格子。
    /// centerOffset 为「转心相对形状锚点」的像素偏移（旋转动画传 fromCur/2 → 盒子居中于 pivot）。</summary>
    public static void DrawItemCellsRotated(CanvasItem canvas, PlacementShape shape, Vector2 pivot, float rot,
        Vector2 centerOffset, Color color, float alpha, int cellSize, Color? borderColor = null)
    {
        canvas.DrawSetTransform(pivot, rot, Vector2.One);
        var cell = (float)cellSize;
        const float inset = 4f;
        var size = new Vector2(cell, cell) - Vector2.One * inset * 2f;

        // 投影：先铺一层偏移动阴影，让转动中的物品也有浮空感
        var shadow = new Color(0f, 0f, 0f, 0.35f);
        foreach (var c in shape.GetOccupiedCells())
            canvas.DrawRect(new Rect2((Vector2)(c * cellSize) - centerOffset + new Vector2(3f, 3f), size), shadow, true);

        foreach (var c in shape.GetOccupiedCells())
        {
            var rect = new Rect2((Vector2)(c * cellSize) - centerOffset, size);
            var fill = color;
            fill.A = alpha;
            var border = borderColor ?? color.Darkened(0.35f);
            border.A = alpha;
            canvas.DrawRect(rect, fill, true);
            canvas.DrawRect(rect, border, false, 1.5f);
        }
        canvas.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// <summary>
    /// 幽灵预览应绘制的占格：从形状占格中过滤掉「目标格不在可放置仓库区内」的部分
    /// （越界 / 不可用凹陷都不生成幽灵块）。手持物品本体不受影响，仍自由跟随鼠标。
    /// </summary>
    public static IEnumerable<Vector2I> GhostOccupiedCells(SpatialInventory inventory, Vector2I anchor,
        PlacementShape shape)
    {
        foreach (var c in shape.GetOccupiedCells())
            if (inventory.IsCellValid(anchor + c))
                yield return c;
    }
}
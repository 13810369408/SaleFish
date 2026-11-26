using Godot;

namespace SaleFish.Inventory;

/// <summary>
/// 背包中的一个放置物：形状（携带当前旋转状态）+ 业务数据钩子。
/// Data 是给上层业务（鱼获、装饰等）挂数据的通道，背包本身完全不解读它。
/// </summary>
public sealed class InventoryItem
{
    /// <summary>当前形状，含旋转状态。旋转通过本类方法进行。</summary>
    public PlacementShape Shape;

    /// <summary>已放置时在容器中的锚点（左上角格子坐标）。脱离容器后此值仅供调试参考。</summary>
    public Vector2I Origin;

    /// <summary>业务数据钩子（例如鱼的数据对象、装饰的配色由上层自行约定类型）。</summary>
    public object? Data;

    /// <summary>已顺时针旋转的 90° 次数（0~3）。渲染层据此旋转贴图「内容」，而不是把图重新拉伸。</summary>
    public int RotationTurns;

    public InventoryItem(PlacementShape shape, object? data = null)
    {
        Shape = shape;
        Data = data;
    }

    /// <summary>顺时针旋转 90°；形状与旋转朝向同步更新。</summary>
    public void RotateClockwise()
    {
        Shape = Shape.RotatedClockwise();
        RotationTurns = (RotationTurns + 1) % 4;
    }
}
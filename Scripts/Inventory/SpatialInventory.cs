using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SaleFish.Inventory;

/// <summary>放置尝试的结果类型。</summary>
public enum PlaceOutcome
{
    /// <summary>成功放入。</summary>
    Success,
    /// <summary>成功放入，且把原来占用目标格的那个物品顶起（将成为新的手持物）。</summary>
    Swap,
    /// <summary>目标格超出空间范围。</summary>
    OutOfBounds,
    /// <summary>目标格落在空间声明的不可用区域（凹陷/凸起）。</summary>
    InvalidArea,
    /// <summary>目标格被 ≥2 个物品占用，无法放置。</summary>
    OverlapMulti,
}

/// <summary>一次放置尝试的结果。</summary>
public sealed class PlaceResult
{
    public PlaceOutcome Outcome;
    /// <summary>仅当 Outcome == Swap 时非空：被顶起、转为手持的原物品。</summary>
    public InventoryItem? Bumped;
}

/// <summary>
/// 容器核心逻辑：负责「放置 / 拿起 / 交换 / 冲突判定」，与任何渲染、输入无关。
/// 规则：
///   1. 目标格无物品 → 直接放入。
///   2. 目标格被恰好 1 个物品占用 → 执行交换：新物品放入，原物品转为手持。
///   3. 目标格被 ≥2 个物品占用 → 放置失败，不做任何变更。
///   4. 任何目标格越界或落在不可用区 → 放置失败。
/// 登记方式：物品按「实例引用」登记，与 Id 无关 —— 同一类型（同 Id，配置系统生成）的多个物品可同时放入，
/// 放置/拿起/顶起都只操作该实例本身。Id 是类型标识，不是唯一键。
/// </summary>
public sealed class SpatialInventory
{
    private readonly StorageSpace _space;
    private readonly InventoryItem?[] _itemByCell;    // cellIndex -> 占用它的物品实例，null 表示空
    private readonly List<InventoryItem> _items = [];

    public SpatialInventory(StorageSpace space)
    {
        _space = space;
        _itemByCell = new InventoryItem?[space.Size.X * space.Size.Y];
    }

    public StorageSpace Space => _space;
    public Vector2I Size => _space.Size;

    /// <summary>当前容器内所有已放置物品（无序）。</summary>
    public IEnumerable<InventoryItem> PlacedItems => _items;

    public bool IsInside(Vector2I c) => _space.IsInside(c);
    public bool IsCellValid(Vector2I c) => _space.IsValid(c);

    private int Idx(Vector2I c) => c.Y * _space.Size.X + c.X;

    /// <summary>查询某个格子上的物品；空格返回 null。</summary>
    public InventoryItem? GetItemAt(Vector2I c) =>
        _space.IsInside(c) ? _itemByCell[Idx(c)] : null;

    /// <summary>只读判定：给定形状放在 anchor 处会得到什么结果（不修改任何状态）。</summary>
    public PlaceOutcome GetPlaceOutcome(PlacementShape shape, Vector2I anchor)
    {
        CollectConflicts(shape, anchor, out var allInside, out var allValid, out var owners);

        if (!allInside) return PlaceOutcome.OutOfBounds;
        if (!allValid) return PlaceOutcome.InvalidArea;
        return owners.Count switch
        {
            0 => PlaceOutcome.Success,
            1 => PlaceOutcome.Swap,
            _ => PlaceOutcome.OverlapMulti,
        };
    }

    /// <summary>尝试放置。成功（含交换）才修改状态；失败不做任何变更。</summary>
    public PlaceResult TryPlace(InventoryItem item, Vector2I anchor)
    {
        var result = new PlaceResult();
        CollectConflicts(item.Shape, anchor, out var allInside, out var allValid, out var owners);

        if (!allInside) { result.Outcome = PlaceOutcome.OutOfBounds; return result; }
        if (!allValid) { result.Outcome = PlaceOutcome.InvalidArea; return result; }

        switch (owners.Count)
        {
            case 0:
                Occupy(item, anchor);
                result.Outcome = PlaceOutcome.Success;
                return result;

            case 1:
            {
                // 交换：先放掉被顶起的物品占用的格子，再放入新物品。
                var bumped = owners.First();
                FreeCells(bumped);
                _items.Remove(bumped);
                Occupy(item, anchor);
                result.Outcome = PlaceOutcome.Swap;
                result.Bumped = bumped;
                return result;
            }

            default:
                result.Outcome = PlaceOutcome.OverlapMulti;
                return result;
        }
    }

    /// <summary>把物品移出容器（拿起到手上）。返回是否成功。</summary>
    public bool RemoveItem(InventoryItem item)
    {
        if (!_items.Contains(item)) return false;
        FreeCells(item);
        _items.Remove(item);
        return true;
    }

    /// <summary>清空容器。</summary>
    public void Clear()
    {
        Array.Fill(_itemByCell, null);
        _items.Clear();
    }

    private void CollectConflicts(PlacementShape shape, Vector2I anchor,
        out bool allInside, out bool allValid, out HashSet<InventoryItem> owners)
    {
        allInside = true;
        allValid = true;
        owners = new HashSet<InventoryItem>(); // 默认引用相等：同一实例只计一次，俩同型实例各算各的
        foreach (var rel in shape.GetOccupiedCells())
        {
            var c = anchor + rel;
            if (!_space.IsInside(c)) { allInside = false; continue; }
            if (!_space.IsValid(c)) { allValid = false; continue; }
            var owner = _itemByCell[Idx(c)];
            if (owner != null) owners.Add(owner);
        }
    }

    private void Occupy(InventoryItem item, Vector2I anchor)
    {
        item.Origin = anchor;
        _items.Add(item);
        foreach (var rel in item.Shape.GetOccupiedCells())
            _itemByCell[Idx(anchor + rel)] = item;
    }

    private void FreeCells(InventoryItem item)
    {
        foreach (var rel in item.Shape.GetOccupiedCells())
            _itemByCell[Idx(item.Origin + rel)] = null;
    }
}
using Godot;
using SaleFish.Inventory;

namespace SaleFish.View;

/// <summary>
/// 手持渲染层：由全局手（HeldItemController autoload）创建并挂在 CanvasLayer(高图层) 下，画在所有仓库之上。
/// 职责（只读全局手的状态，不持有输入）：
///  ▪ 幽灵预览——指针所在最上层仓库的落点（用目标仓库的容器与格大小判定）；
///  ▪ 手持物品本体——全局坐标直接画（层自身无偏移，本地=全局），常驻最上层。
/// 过去手持本体由各仓库自行绘制：后挂上来的仓库（如 CreateStorage 的新仓库）会盖住旧仓库画的本体，
/// 鼠标尚未移进新仓库时物品就已被挡住。拆到本层后「手持永远在各仓库之上」由图层序保证。绘制细节
/// （格子色块/整图/投影）复用 ItemRenderer，坐标一律用全局。
/// </summary>
public sealed partial class HandRenderLayer : Control
{
    // 幽灵预览配色（与旧 DrawHeldState 一致）
    private static readonly Color SuccessTint = new("56d364");
    private static readonly Color SwapTint = new("f6c945");   // 黄 = 重合恰好 1 个块（可交换）
    private static readonly Color SwapWash = new(1f, 0.85f, 0.30f, 0.50f);   // 被顶起那块的重叠格更亮
    private static readonly Color ConflictTint = new("e05050");

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var hand = HeldItemController.Instance;
        if (hand?.HeldItem == null) return;

        // 指针所在的最上层仓库：只有它提供落点语义（容器 + 格大小 + 网格原点）；不在任何仓库上只画本体。
        var top = WarehouseRegistry.TopmostAt(GetGlobalMousePosition());
        if (top != null) DrawGhost(top, hand);
        DrawBody(hand);
    }

    /// <summary>幽灵预览：目标格着色（绿=可放 黄=可交换 红=冲突）+ 蓝色重叠提示。绘制在目标仓库的网格上。</summary>
    private void DrawGhost(InventoryView view, HeldItemController hand)
    {
        var item = hand.HeldItem!;
        // 鼠标在目标仓库视图内的本地坐标（层在本视图上时 GetLocalMousePosition 是层的坐标，需换算）
        var mouseLocal = GetGlobalMousePosition() - view.GetGlobalRect().Position;
        var hoverCell = view.ToSubCell(mouseLocal, hand.GrabSnapFraction);
        var anchor = hoverCell - hand.GrabCell;
        var outcome = view.Inventory.GetPlaceOutcome(item.Shape, anchor);

        var tint = outcome switch
        {
            PlaceOutcome.Success => SuccessTint,
            PlaceOutcome.Swap => SwapTint,
            _ => ConflictTint,
        };

        var cell = (float)view.CellSize;
        var inner = new Vector2(cell, cell) - Vector2.One * 4f;
        // 网格原点转到全局：目标仓库网格左上角（全局）+ 目标格坐标
        var originGlobal = view.GetGlobalRect().Position + view.CellToPixel(anchor);

        // 幽灵打底：每个目标格先铺设结果色；被物品占用的格加深。底色刻意压淡，让上面的手持本体可辨。
        // 目标格不在可放置仓库区内（越界/凹陷）的占格不生成幽灵块——悬在仓库外时只画区内那一部分。
        foreach (var c in ItemRenderer.GhostOccupiedCells(view.Inventory, anchor, item.Shape))
        {
            var rect = new Rect2(originGlobal + new Vector2(c.X * cell, c.Y * cell) + Vector2.One * 2f, inner);
            var occupiedNow = view.Inventory.GetItemAt(anchor + c) != null;
            var baseColor = tint;
            baseColor.A = occupiedNow ? 0.45f : 0.22f;
            DrawRect(rect, baseColor, true);

            if (outcome == PlaceOutcome.Swap && occupiedNow)
            {
                // 交换：与将被顶起的那个物品重合的格，再叠一层蓝色提示
                DrawRect(rect, SwapWash, true);
            }
        }
    }

    /// <summary>手持物品本体：全局坐标直接画（层无偏移，本地=全局），近不透明 + 白描边 + 投影。
    /// 格大小用「拿起时的格大小」（手抓着就是那个尺度），跨仓库不会随悬停仓库变化。</summary>
    private void DrawBody(HeldItemController hand)
    {
        var item = hand.HeldItem!;
        if (hand.EnableRotateAnimation && hand.IsRotateAnimating)
        {
            // 旋转动画：绕「当前锚点」转——锚点即当前鼠标，动画全程钉在原处，块绕它转 0→90°。
            // 图片旋转只能绕图片自身中心（DrawSetTransform 的 pivot），所以图片（盒）中心要额外算：
            // 盒中心 = 锚点 − R_θ(g₀)，g₀ = 锚点相对旋转前盒中心的固定向量（1006 为 (−26,0)），
            // 即盒中心绕鼠标画半径 |g₀| 的圆弧（45° 时 X/Y 分量各 = |g₀|/√2）。离锚点越远的格甩动
            // 越大——「以锚点为轴」的必然；但锚点恒钉在鼠标下，动画结束位与正常跟随完全重合、零跳变。
            // DrawOffset 随内容一起转。
            var dur = Mathf.Max((float)hand.RotateAnimationDuration, 0.001f);
            var p = Mathf.Clamp(hand.RotateAnimElapsed / dur, 0f, 1f);
            var eased = p * p * (3f - 2f * p); // smoothstep：起步缓、收尾缓

            var fromShape = hand.RotateFromShape;
            var hasTex = ItemRenderer.TryGetDefTexture(item, out var rotTex, out var rotDef);
            var fromCur = ItemRenderer.SpriteCurrentSize(fromShape, rotDef, hand.GrabCellSize, item.RotationTurns - 1);
            // 锚点 = 当前鼠标（全局坐标，HeldVisPos + GrabVisualOffset 已全局）——层无偏移，直接用。
            var pivot = hand.HeldVisPos + hand.GrabVisualOffset;
            var gFromCenter = hand.RotateFromGrabOffset - fromCur / 2f;  // 锚点距旋转前盒中心的固定向量
            var center = pivot - ItemRenderer.RotateVector(gFromCenter, eased * Mathf.Pi / 2f);

            if (hasTex)
            {
                // 整图：内容尺寸 baseSize = 未旋转原图铺满盒子的尺寸，绕滑动的盒中心转到最终朝向。
                var baseSize = ItemRenderer.SpriteBaseSize(fromCur, item.RotationTurns - 1);
                var totalRad = (item.RotationTurns - 1 + eased) * Mathf.Pi / 2f;
                var localOrigin = -baseSize / 2f + rotDef.DrawOffset * hand.GrabCellSize;
                ItemRenderer.DrawItemSpriteRotated(this, rotTex, center, baseSize, localOrigin, totalRad,
                    new Color(1f, 1f, 1f, 0.95f));
            }
            else
                ItemRenderer.DrawItemCellsRotated(this, fromShape, center, eased * Mathf.Pi / 2f,
                    fromCur / 2f, ItemRenderer.FallbackCellColor, 0.95f, hand.GrabCellSize, Colors.White);
        }
        else
        {
            // HeldVisPos 是全局坐标：HeldVisPos 直接画（层无偏移，本地=全局）。
            var followPx = hand.HeldVisPos;
            if (ItemRenderer.TryGetDefTexture(item, out var tex, out var def))
            {
                // 整张贴图：不画按格切分的投影层（那块淡灰格子）；贴图内容随朝向旋转，不重新拉伸
                var cur = ItemRenderer.SpriteCurrentSize(item.Shape, def, hand.GrabCellSize, item.RotationTurns);
                var baseSize = ItemRenderer.SpriteBaseSize(cur, item.RotationTurns);
                ItemRenderer.DrawItemSpriteRotated(this, tex, followPx + cur / 2f, baseSize,
                    -baseSize / 2f + def.DrawOffset * hand.GrabCellSize,
                    item.RotationTurns * Mathf.Pi / 2f, new Color(1f, 1f, 1f, 0.95f));
            }
            else
            {
                ItemRenderer.DrawItemShadow(this, item.Shape, followPx, 0.35f, hand.GrabCellSize); // 占位色块才有按格投影
                ItemRenderer.DrawItemCells(this, item.Shape, followPx, ItemRenderer.FallbackCellColor, 0.95f,
                    hand.GrabCellSize, Colors.White);
            }
        }
    }
}
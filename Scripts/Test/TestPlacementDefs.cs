using System.Linq;
using Godot;
using SaleFish.Inventory;
using SaleFish.View;

namespace SaleFish.Test;

/// <summary>
/// 局外配置（Resource .tres）测试：验证配置定义 → 数据层的转换，以及样例 .tres 文件能被正确解析。
/// 运行方式：godot --headless --path &lt;project&gt; res://Scenes/TestPlacementDefs.tscn --quit-after 120
/// </summary>
public sealed partial class TestPlacementDefs : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        Run();
        GD.Print($"[DefTest] PASS={_passed} FAIL={_failed}");
        GD.Print(_failed == 0 ? "[DefTest] ALL PASSED" : "[DefTest] HAS FAILURES");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(string name, bool cond)
    {
        if (cond) { _passed++; GD.Print($"[PASS] {name}"); }
        else { _failed++; GD.Print($"[FAIL] {name}"); }
    }

    private void Run()
    {
        // 1. T 形图案 → 数据层形状（验证自定义形状配置入口）
        var t = new PlacementItemTypeDef { DisplayName = "T", ShapePattern = new[] { "XXX", ".X." } };
        var shape = t.ToShape();
        Check("T 形尺寸 3×2", shape.Width == 3 && shape.Height == 2);
        Check("T 形占 4 格", shape.CellCount == 4);
        Check("T 形图案一致", shape.ToPattern().SequenceEqual(new[] { "XXX", ".X." }));

        // 2. def → item：Data 钩子（Data 携带定义供视图取显示信息）
        var item = t.ToItem();
        Check("ToItem 的 Data 挂回定义", ReferenceEquals(item.Data, t));
        Check("ToItem 形状正确", item.Shape.CellCount == 4);

        // 3. 显示字段可配置且默认值正确（无贴图 / 倍数 1 / 偏移 0；占位色已删，无贴图统一白色）
        Check("默认无贴图(整图现在不生效)", t.Texture == null);
        Check("DisplaySize 默认 (0,0) = 跟随形状格数", t.DisplaySize == Vector2.Zero);

        // 旋转时 DisplaySize 随朝向交换宽高（显式配置是「物品自身朝向」的格数）；
        // 未配置(跟随形状)时形状已含当前朝向、不做重复交换。
        var sized = new PlacementItemTypeDef { DisplayName = "大小鱼", ShapePattern = new[] { "xxx" }, DisplaySize = new Vector2(3f, 2f) };
        Check("DisplaySize 旋转 0° = 3×2 格",
            ItemRenderer.SpriteCurrentSize(sized.ToShape(), sized, 20, 0) == new Vector2(60, 40));
        Check("DisplaySize 旋转 90° = 2×3 格（宽高互换）",
            ItemRenderer.SpriteCurrentSize(sized.ToShape(), sized, 20, 1) == new Vector2(40, 60));
        var noSize = new PlacementItemTypeDef { ShapePattern = new[] { "xxx" } };
        var rotatedShape = noSize.ToShape().RotatedClockwise();   // 3×1 → 1×3
        Check("未配 DisplaySize：跟随形状且旋转时不重复换（形状已含朝向）",
            ItemRenderer.SpriteCurrentSize(rotatedShape, noSize, 20, 1) == new Vector2(20, 60));
        Check("DrawOffset 默认 0", t.DrawOffset == Vector2.Zero);

        // 4. StorageTypeDef：图案为空 → 不生成空间（ToSpace 返回 null）；非空 → 以图案为准（图案自带尺寸）
        var empty = new StorageTypeDef { Id = 2 };
        Check("空图案不生成空间", empty.ToSpace() == null);

        var shaped = new StorageTypeDef { Id = 3, ValidPattern = new[] { "##..", "####" } };
        var shapedSpace = shaped.ToSpace();
        Check("图案定尺寸(4×2)", shapedSpace != null && shapedSpace.Size == new Vector2I(4, 2));
        Check("图案保留无效格", shapedSpace != null
              && !shapedSpace.IsValid(new Vector2I(2, 0)) && shapedSpace.IsValid(new Vector2I(0, 1)));

        // 5. CellSize 可配置（不同格大小的仓库复用同一套物品定义）
        Check("CellSize 可配置", new StorageTypeDef { CellSize = 64 }.CellSize == 64);

        // 5.5. 地面显示字段：贴图默认空（保持内置配色），尺寸/偏移默认居中
        var look = new StorageTypeDef { Id = 4 };
        Check("单格/放置后/背景贴图默认空",
            look.CellTexture == null && look.CellTextureWithItem == null && look.BackgroundTexture == null);
        Check("背景尺寸默认 10×10 格、偏移默认居中",
            look.BackgroundTextureSize == new Vector2(10, 10) && look.BackgroundTextureOffset == Vector2.Zero);

        // 6. 真实样例 .tres 文件可加载（局外配置的实际入口）
        var mainBag = ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/MainBag.tres");
        // 格大小由配置文件决定（用户可改），只断言合法正整数，不硬编码具体值——与图案同理，改配置不连累本测试
        Check("主背包 .tres 可加载(格大小为正)", mainBag != null && mainBag.CellSize > 0);
        var mainBagSpace = mainBag?.ToSpace();
        // 尺寸由图案本身推导（宽=最长行、高=行数），不硬编码具体数值，用户改仓库形状不会连累本测试
        Check("主背包图案→空间(宽=最长行, 高=行数)", mainBagSpace != null
              && mainBagSpace.Size.X == mainBag!.ValidPattern.Max(p => p.Length)
              && mainBagSpace.Size.Y == mainBag.ValidPattern.Length);

        var seaBassItem = ResourceLoader.Load<PlacementItemTypeDef>("res://Data/Inventory/Items/SeaBassItem.tres");
        Check("海鲈鱼物品 .tres 可加载（2×3 共 6 格，DisplaySize=3×2）",
            seaBassItem != null && seaBassItem.DisplayName.Length > 0
              && seaBassItem.ToShape().CellCount == 6 && seaBassItem.DisplaySize == new Vector2(3f, 2f));

        var boxfishItem = ResourceLoader.Load<PlacementItemTypeDef>("res://Data/Inventory/Items/BoxfishItem.tres");
        Check("角箱魨物品 .tres 加载（2×2 图案）", boxfishItem != null
              && boxfishItem.ToShape().Width == 2 && boxfishItem.ToShape().CellCount == 4);

        var fancy = ResourceLoader.Load<StorageTypeDef>("res://Data/Inventory/Storage/FancyBox.tres");
        var fancySpace = fancy?.ToSpace();
        Check("异形仓库 .tres 以图案为准", fancySpace != null
              && fancySpace.Size == new Vector2I(7, 4) && !fancySpace.IsValid(new Vector2I(3, 0)));

        // 7. 宽度不足的短行自动以空位填充（不再要求每行等宽）
        var ragged = new PlacementItemTypeDef { ShapePattern = new[] { "xx", "x" } };
        var rs = ragged.ToShape();
        Check("物品图案短行自动补空位(2×2 占 3 格)", rs.Width == 2 && rs.Height == 2 && rs.CellCount == 3);
        Check("补出的格未被占用", !rs.IsCell(1, 1) && rs.IsCell(1, 0));

        var rspace = new StorageTypeDef { ValidPattern = new[] { "###", "#" } };
        var rsp = rspace.ToSpace();
        Check("空间图案短行自动补位", rsp != null && rsp.Size == new Vector2I(3, 2)
              && rsp.IsValid(new Vector2I(2, 0)) && !rsp.IsValid(new Vector2I(1, 1)));
    }
}
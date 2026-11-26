using System;
using Godot;

namespace SaleFish.Inventory;

/// <summary>
/// 储存空间类型定义：局外可配置（Godot Resource，可在检查器里新建/编辑）。
/// 系统会复用在多个地方（背包、店仓…各有不同格大小），因此「每格像素」也作为配置项放在这里。
///
/// 有效区规则：完全由 ValidPattern 决定 —— 行数=高，最长行=宽，短行自动按不可用格填充；
///            ValidPattern 为空 = 不生成空间（ToSpace 返回 null，由调用方回退或提示配置错误）。
/// </summary>
/// <example>
/// 矩形仓库：{ Id = 2001, CellSize = 52, ValidPattern = new[] { "##########", …共十行 } }
/// 异形仓库：{ Id = 2002, CellSize = 64, ValidPattern = new[] { "###.###", "#######" } }
/// </example>
[GlobalClass]
public sealed partial class StorageTypeDef : Resource
{
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名。</summary>
    [Export] public string DisplayName = "";

    /// <summary>每格像素尺寸：同一套物品定义换用它即可适配不同大小的格子。</summary>
    [Export] public int CellSize = 52;

    /// <summary>有效区图案：'#'/'X' 为可用格，其余字符为不可用（凹陷/凸起）。
    /// 行数=高，最长行=宽，短行欠缺部分自动按不可用格填充。为空 = 不生成空间。</summary>
    [Export] public string[] ValidPattern = Array.Empty<string>();

    // ---- 交互字段（数据层/视图层都会读） ----

    /// <summary>能否整仓拖动（空手按住仓库背景/空格子拖走面板）。默认 false = 面板固定不可拖。</summary>
    [Export] public bool CanDrag;

    // ---- 关闭按钮（可选）：不为空时，打开该仓库会额外生成一个关闭按钮 ----

    /// <summary>关闭按钮贴图：为空 = 不生成关闭按钮。</summary>
    [Export] public Texture2D? CloseButtonTexture;

    /// <summary>关闭按钮位置：相对仓库面板左上角的像素偏移（面板 = 视图 Position，即背景盒左上角）。</summary>
    [Export] public Vector2 CloseButtonPosition;

    /// <summary>关闭按钮尺寸（像素）。</summary>
    [Export] public Vector2 CloseButtonSize = new(48, 48);

    // ---- 地面显示字段：仅渲染层（InventoryView）读取，数据层不触碰；全部留空则保持内置配色 ----

    /// <summary>单格底纹贴图（每个「有效格」拉伸铺满该格，边框线仍画在上层）；为空时用纯色格填充。</summary>
    [Export] public Texture2D? CellTexture = null;

    /// <summary>放置后的单格底纹：格子上已放置物品时改用本贴图（仍拉伸铺满该格），空置格仍用 CellTexture。
    /// 为空时回退到 CellTexture（再无贴图则回到纯色填充）。</summary>
    [Export] public Texture2D? CellTextureWithItem = null;

    /// <summary>整背景贴图：按 BackgroundTextureSize×CellSize 的尺寸、以仓库背景区中心为基准绘置；
    /// 凹陷/不可用格露出本贴图。为空时维持纯色底座 + 深色凹陷。</summary>
    [Export] public Texture2D? BackgroundTexture = null;

    /// <summary>背景贴图尺寸（单位=格）：实际像素尺寸 = 值 × CellSize。默认 (10,10)，按需自行调整。</summary>
    [Export] public Vector2 BackgroundTextureSize = new(10, 10);

    /// <summary>背景贴图相对仓库背景区中心的偏移（单位=格，像素 = 值 × CellSize）。
    /// 默认 (0,0) = 贴图中心正好对齐背景区中心；需要微调位置时在此设置。</summary>
    [Export] public Vector2 BackgroundTextureOffset;

    /// <summary>转换为数据层空间；ValidPattern 为空时返回 null（不生成，由调用方决定回退或提示配置错误）。</summary>
    public StorageSpace? ToSpace() =>
        ValidPattern.Length > 0 ? StorageSpace.FromPattern(ValidPattern) : null;
}
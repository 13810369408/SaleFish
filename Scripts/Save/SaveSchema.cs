using System.Collections.Generic;

namespace SaleFish.Save;

/// <summary>
/// 存档数据结构（纯 C# 数据类，不引 Godot 类型）。核心约定：
///  ▪ 运行态是 def 引用（Godot Resource 实例），存档里只存「res:// 路径 + 简单数值」；
///  ▪ 读档时按路径 ResourceLoader.Load 重取 def、再重建对象 —— 不把 Texture/策略这类运行态对象写盘；
///  ▪ 序列化走 System.Text.Json 的公共字段模式（见 SaveService.Options）。
/// 各字段带默认值，旧存档读进缺字段也不崩。
/// </summary>

/// <summary>存档里记录的玩家阶段（与 MainStage.GamePhase 对应；Fishing/Selling 是过程态，不落盘）。</summary>
public enum SavePhase
{
    Ready = 0,
    FreeMove = 1,
    PrepareShop = 2,
}

/// <summary>一份存档文件。</summary>
public sealed class SaveFile
{
    /// <summary>存档格式版本：将来字段变更时用它在读档处做迁移/补默认。
    /// v2 = 新增 <see cref="InFreeMove"/>（v1 旧档缺该字段 → 用字段默认值 true，即进场自由移动）。
    /// v3 = 新增 <see cref="PlayerX"/>/<see cref="PlayerY"/>（旧档为 null → 用场景默认出生点）。
    /// v4 = 新增 <see cref="Phase"/>（玩家阶段，替代 bool）+ <see cref="ShopPrices"/>/<see cref="LastSellSeed"/>
    ///      + 图鉴价格 <see cref="EncyclopediaSave.Price"/>；旧档 Phase = -1 → 回退看 InFreeMove。</summary>
    public int Version = 4;

    /// <summary>保存时刻（unix 秒），槽位列表显示用。</summary>
    public long SavedAt;

    /// <summary>存档时的语言设置（读档时写回 Localizer）。</summary>
    public string Language = "";

    /// <summary>金币（本期只建变量与读写口，卖鱼流程后续接）。</summary>
    public long Money;

    /// <summary>累计钓上条数（图鉴计数用）。</summary>
    public long TotalCaught;

    /// <summary>存档时玩家是否处于「自由移动」阶段（GamePhase.FreeMove）。
    /// **v4 起仅用于读旧档兼容**：新存档以 <see cref="Phase"/> 为准，这里同步写一份方便降级/排查。</summary>
    public bool InFreeMove = true;

    /// <summary>存档时的玩家阶段（见 <see cref="SavePhase"/>）。-1 = 旧档没记录 → 回退看 <see cref="InFreeMove"/>。</summary>
    public int Phase = -1;

    /// <summary>卖鱼定价（按鱼种）：价格板上玩家填的标价，准备开店阶段之后一直有效，随存档保存。</summary>
    public List<ShopPriceSave> ShopPrices = [];

    /// <summary>上一场卖鱼的确定性种子（hash 派生，见 Shop.SellService.DeriveSeed）。-1 = 还没卖过。</summary>
    public long LastSellSeed = -1;

    /// <summary>玩家位置（世界坐标 X）。null = 没记录（旧档 / 从没进过主关卡）→ 读档用场景默认出生点。</summary>
    public float? PlayerX;

    /// <summary>玩家位置（世界坐标 Y）。与 <see cref="PlayerX"/> 成对。</summary>
    public float? PlayerY;

    /// <summary>图鉴记录：遇到/钓到的鱼（有条目 = 已见，Caught 置位 = 钓到过）。</summary>
    public List<EncyclopediaSave> Encyclopedia = [];

    /// <summary>存档瞬间手持的物品（null = 空手）。不存的话「钓上即存档」时刚钓的鱼会丢。</summary>
    public PlacedItemSave? Held;

    /// <summary>各仓库内容（只存打开过一次的仓库；从未创建的仓库 = 默认空布局，无需存）。</summary>
    public List<WarehouseSave> Warehouses = [];
}

/// <summary>图鉴单条记录。</summary>
public sealed class EncyclopediaSave
{
    public long FishId;
    /// <summary>已记录的最浅所见深度（px，水面往下为正）；只扩不缩。</summary>
    public float DepthMin;
    /// <summary>已记录的最深所见深度。</summary>
    public float DepthMax;
    /// <summary>是否钓到过（图鉴该鱼变全彩、显示真名）。</summary>
    public bool Caught;
    /// <summary>历史最高成交价（0 = 还没卖过，图鉴价格行显示"?"）。</summary>
    public int Price;
}

/// <summary>一种鱼的玩家标价（价格板）。</summary>
public sealed class ShopPriceSave
{
    /// <summary>FishDef.Id。</summary>
    public long FishId;
    /// <summary>玩家填的标价（0~999）。</summary>
    public int Price;
}

/// <summary>一座仓库的存档内容。</summary>
public sealed class WarehouseSave
{
    /// <summary>仓库布局定义路径（res://Data/Inventory/Storage/*.tres）：读档时据此找场景里对应的仓库按钮。</summary>
    public string StorageDefPath = "";

    /// <summary>面板左上角位置（UI 宿主本地坐标，与 InventoryView.Position 同一空间）。</summary>
    public float PosX;
    public float PosY;

    public List<PlacedItemSave> Items = [];
}

/// <summary>一个已放置/手持物品的存档（只存标识与位置，不存 Godot 对象）。</summary>
public sealed class PlacedItemSave
{
    /// <summary>物品定义路径（res://Data/Inventory/Items/*.tres）：物品的唯一标识。</summary>
    public string ItemDefPath = "";

    /// <summary>已放置时的锚点（左上角格子坐标）。</summary>
    public int OriginX;
    public int OriginY;

    /// <summary>已顺时针旋转的 90° 次数（0~3）。</summary>
    public int RotationTurns;
}
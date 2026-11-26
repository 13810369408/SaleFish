using System;
using Godot;
using SaleFish.Inventory;
using SaleFish.Localization;
using SaleFish.View;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼的尺寸档（图鉴 SizeRow 等展示用；还有数据引导：体型与价格/挣扎强度大致相关）。
/// 图鉴显示文字走本地化键 fishsize.&lt;档位&gt;（zh/en 在 strings.csv）。
/// </summary>
public enum FishSize
{
    /// <summary>超小型（如沙丁鱼）。</summary>
    UltraSmall = 0,
    /// <summary>小型。</summary>
    Small = 1,
    /// <summary>中型。</summary>
    Medium = 2,
    /// <summary>大型。</summary>
    Large = 3,
    /// <summary>超大型（如翻车鱼）。</summary>
    UltraLarge = 4,
}

/// <summary>
/// 鱼定义（局外配置，Godot Resource）：一种鱼的完整参数 + 「每鱼行为」的两个可填策略槽。
///
/// 三个「能钓到」声明分别决定哪种钓具能钓到本鱼，**空数组 = 对应钓法一律钓不到，不是放宽限制**：
///  ▪ Hooks        — 允许的鱼钩（空 = 任何钩子都钓不到它）；
///  ▪ Baits        — 允许的真饵（空 = 用任何真饵都钓不到它）；
///  ▪ LiveBaitTags — 允许的饵鱼标签（鱼当鱼饵，空 = 用任何鱼饵鱼都钓不到它）。
/// 另有一个 Tags —— 本鱼自身携带的标签（决定「本鱼被当饵鱼时能钓到谁」）。饵鱼匹配看标签而非具体鱼：
/// 用鱼 A 当饵鱼时，A.Tags 与目标鱼 B.LiveBaitTags 有交集 → 咬钩。
///
/// 上钩后的背包形态：直接引用一个 PlacementItemTypeDef（占格形状 / 贴图 / 显示参数全部复用），
/// 入库时用 ItemDef.ToItem() 生成物品；鱼定义自身不再重复轮廓数据。SellPrice 为售价。
/// 在 `res://Data/Fishing/Fish/` 下新建 Resource 并选本类即可配置一种鱼。
///
/// **策略槽（一条鱼的两种行为，每鱼种可单独写）**——在槽里「快速载入→新建」选一个策略类：
///  ▪ BehaviorStrategy —— 平时行为（巡逻/追饵/返回）的每帧决策（Fish 每帧调用，FishBehaviorBase）；
///  ▪ Struggle        —— 上钩挣扎的每帧决策（Hook 每帧调用，FishStruggleBase，另有一个 OnHooked 开场时机）。
/// 空槽语义：BehaviorStrategy 空 = 鱼不动只漂着；Struggle 空 = 上钩后安静待收线（各打一条仅测试可见的警告）。
/// 内置两个策略可直接挂（ClassicBehavior = 数据驱动巡逻/追饵/返回；ClassicStruggle = 体力/耐力/拉力数值机），
/// 数值手感与原来完全一致，普通鱼挂上即可。要写专属行为：在 C# 里建 FishBehaviorBase / FishStruggleBase
/// 的 [GlobalClass] 派生类（如 `SharkBehavior`），到槽里「新建」选它——Godot 原生支持
/// 在 Resource 字段里选抽象基类的具体子类（等价 Unity 的 Odin + [SerializeReference]），
/// 每个鱼种的专属策略作为独立资源存在，可被多个 FishDef 复用。
/// 无论内置还是自定义，纯数值公共输入（MaxStamina/MaxEndurance/PullSpeed）留在这份定义上，
/// 策略经 ctx.Def 读取；行为参数（巡逻深度/速度/感应等）直接写在策略资源自己身上
/// （ClassicBehavior 的 7 个导出参数）——没有独立的 Behavior 数据 Resource。
/// </summary>
[GlobalClass]
public partial class FishDef : Resource
{
    [ExportCategory("基础信息")]
    /// <summary>唯一 Id。</summary>
    [Export] public long Id;

    /// <summary>显示名（鱼名，回退用）。</summary>
    [Export] public string DisplayName = "";

    /// <summary>尺寸档（图鉴 SizeRow 显示该档位的本地化文字，键 fishsize.&lt;档位&gt;）。</summary>
    [Export] public FishSize Size = FishSize.UltraSmall;

    /// <summary>本地化键（可选）：非空时 图鉴/钓上状态栏 里这条鱼的显示名改走 Localizer.T(key)，
    /// 空 = 用 <see cref="DisplayName"/>。想多语言一条鱼：在每张语言表里加同一个 key 即可。</summary>
    [Export] public string LocalizationKey = "";

    /// <summary>本地化后的显示名：配了 LocalizationKey 查表（缺 key 回退默认语言、再没有回 DisplayName）；
    /// 没配 LocalizationKey 就用 DisplayName。</summary>
    public string LocalizedName
        => LocalizationKey.Length > 0 ? Localizer.Lookup(LocalizationKey) ?? DisplayName
                                      : DisplayName;

    /// <summary>售价（卖出价格）。</summary>
    [Export] public float SellPrice;

    /// <summary>背包形态定义：入库物品的占格形状 / 贴图 / 显示参数；null = 该鱼暂不能入库。</summary>
    [Export] public PlacementItemTypeDef? ItemDef;

    [ExportCategory("动画")]
    /// <summary>动画精灵表（SpriteFrames 精灵表资源，一个游泳动画 + 若干帧）：生成鱼时赋给 prefab 里的
    /// AnimatedSprite2D 并开播。null = 无动画（鱼不可见，纯逻辑）。</summary>
    [Export] public SpriteFrames? SpriteFrames;

    /// <summary>正常播放速度（AnimatedSprite2D.SpeedScale 倍率）：平时自由游动 / 上钩但安静待收线时用。</summary>
    [Export] public float NormalPlaySpeed = 1f;

    /// <summary>挣扎时播放速度（SpeedScale 倍率）：上钩且正在挣扎时用。</summary>
    [Export] public float StrugglePlaySpeed = 1f;

    /// <summary>虚弱时播放速度（SpeedScale 倍率）：上钩且体力耗尽（挣扎不动了）时用。</summary>
    [Export] public float ExhaustedPlaySpeed = 1f;

    /// <summary>精灵显示缩放（等比）：把精灵表原图缩放到世界尺寸（默认 6 = 放大 6 倍，所有鱼统一）。
    /// 同时用它把碰撞体（胶囊）按「最大帧尺寸 × SpriteScale」自动适配，保证碰撞与视觉一致。</summary>
    [Export] public float SpriteScale = 6f;

    /// <summary>转身时长（秒，默认 0.2，每种鱼可单独配）：鱼每次左右朝向翻转时，精灵的 Scale.X 在
    /// +SpriteScale ↔ −SpriteScale 之间<b>线性</b>过渡（经过 0 = 鱼身被压成一条竖线，即 2D 转身动画）所用的时间。
    /// 只作用于左右翻转；上钩后被拖行时身体倾斜角的变化不做过渡（即时对齐运动方向）。
    /// 0 = 瞬时翻转（旧行为）。</summary>
    [Export] public float TurnDuration = 0.2f;

    /// <summary>鱼嘴在精灵图里的像素位置（原图未缩放，相对图片<b>左上角 (0,0)</b>：x 向右、y 向下，第一列依次
    /// (0,0)(0,1)(0,2)…；以最大帧尺寸为参考）。运行时减去「图片中心（最大帧尺寸/2）」再 × SpriteScale
    /// 得到相对鱼中心的世界偏移（按朝向镜像 X），用作追饵目标修正、咬钩判定与上钩挂载的锚点
    /// （鱼嘴对准饵/钩子，而不是鱼身中心）。
    /// 默认哨兵 (-1,-1) = 未配置（嘴 = 鱼中心，旧行为）；只有<b>精确等于</b> (-1,-1) 才当未配置，
    /// 其余值（含负分量，例如嘴贴/略超出图片边缘）一律照用。</summary>
    [Export] public Vector2 MouthOffset = new Vector2(-1f, -1f);

    [ExportCategory("钓鱼相关")]
    /// <summary>允许钓到本鱼的鱼钩列表（空 = 任何钩子都钓不到）。</summary>
    [Export] public FishingHookDef[] Hooks = Array.Empty<FishingHookDef>();

    /// <summary>允许钓到本鱼的真饵列表（空 = 用任何真饵都钓不到）。</summary>
    [Export] public BaitDef[] Baits = Array.Empty<BaitDef>();

    /// <summary>本鱼自身携带的标签（例：沙丁鱼 = [超小型]）：当本鱼被当作饵鱼时，靠这些标签决定
    /// 「能被哪些鱼咬」——与目标鱼的 LiveBaitTags 求交集，命中即咬。空 = 无标签（当饵鱼也钓不到任何鱼）。</summary>
    [Export] public FishTagDef[] Tags = Array.Empty<FishTagDef>();

    /// <summary>允许钓到本鱼的饵鱼标签（鱼当鱼饵，空 = 用任何鱼饵鱼都钓不到）。
    /// 例：海鲈鱼 = [超小型]，则任何「携带超小型标签的鱼」当饵都能钓到它。</summary>
    [Export] public FishTagDef[] LiveBaitTags = Array.Empty<FishTagDef>();

    /// <summary>体力最大值（上钩挣扎时的「血条」）：刚钓到该鱼时体力 = 该值。挣扎中每秒 −1，归零立即停止挣扎、且之后不再挣扎。</summary>
    [Export] public float MaxStamina = 10f;

    /// <summary>耐力最大值：刚钓到该鱼时耐力 = 该值。挣扎中每秒 −1；低于满值 50% 时每 0.5s 判一次是否停止，
    /// 停止概率随耐力降低线性增大；停止后每秒 +2 恢复。用于「越折腾越容易停下来歇口气」的节奏。</summary>
    [Export] public float MaxEndurance = 10f;

    /// <summary>挣扎拖速（px/s，每种鱼单独配）：挣扎时拖着鱼钩往远处跑的速度。按住左键施力时减半（换来拉力上涨）。</summary>
    [Export] public float PullSpeed = 120f;

    /// <summary>上钩瞬间把「收线水面间隙 X」改成该值（px）：越大 = 上钩后钩子被压制在水下越深，
    /// 收线只能横着把鱼拽到锚点正下方（±LiftUnlockRange）才进入最终拉升区、把鱼拉起钓上。
    /// 默认 -1 = 不改（沿用钩子当前值）；负值哨兵写法同 MouthOffset（[Export] 无法直接表达 float?）。
    /// 挣扎策略上钩后仍可用 FishContext.ReelSurfaceGap 再覆盖，脱钩/钓上后自动重设回钩子初始值。</summary>
    [Export] public float HookupSurfaceGap = -1f;

    [ExportCategory("策略")]
    /// <summary>平时行为策略（FishBehaviorBase 子类）：巡逻/追饵/返回的每帧决策由它接管。
    /// 挂内置 ClassicBehavior = 数据驱动巡逻/追饵/返回（7 个行为参数直接写在这个策略实例上，
    /// 每帧经 ctx.BehaviorData 打包进鱼）；挂自定义策略 = 该鱼种专属行为（每帧完全由你写）；
    /// null = 鱼不动，只在水里漂着（生成时打一条警告）。</summary>
    [Export] public FishBehaviorBase? BehaviorStrategy;

    /// <summary>上钩挣扎策略（FishStruggleBase 子类）：上钩后每帧决策由它接管。
    /// 挂内置 ClassicStruggle = 体力/耐力/拉力数值机（数值读 MaxStamina/MaxEndurance/PullSpeed + 鱼线最大拉力）；
    /// 挂自定义策略 = 该鱼种专属挣扎（每帧完全由你写）；null = 上钩后安静待收线（上钩时打一条警告）。</summary>
    [Export] public FishStruggleBase? Struggle;

    [ExportCategory("挣扎效果")]
    /// <summary>挣扎时的震屏预设（ScreenShakePreset 资源）：每一轮挣扎开始（Struggling 变 true）时自动震屏。
    /// 走全局 ScreenShaker.Primary 触发（主关卡相机下挂的震屏组件）；null = 挣扎不震屏。</summary>
    [Export] public ScreenShakePreset? StruggleScreenShake;

    /// <summary>挣扎时的特效 prefab（PackedScene，prefab 根节点应带脚本：实现 Play() 播一次 + Finished 信号提示播完）：
    /// 每一轮挣扎开始（Struggling 变 true）时在钩子当前位置实例化并播放，播完自动回收对象池。
    /// 内置 fx_ring_burst.tscn（勉强达到这个约定——但注意它带 AutoPlay，用对象池复用时需先关 AutoPlay 再手动 Play）。
    /// null = 挣扎不播特效。</summary>
    [Export] public PackedScene? StruggleFxPrefab;
}
using System;
using Godot;

namespace SaleFish.Fishing;

/// <summary>
/// 鱼（Area2D）：按行为数据（来自策略的 FishBehaviorData）的参数在水里自主游动的一条鱼。状态机：
///  ▪ Patrol（巡逻）——在「出生点 ± 左右巡逻范围」×「水面下深度区间」的矩形里随机选目标点巡游；
///  ▪ Chase（追饵）——饵（钩子上的鱼饵/活饵）距鱼 < 感知半径时进入，朝饵移动（追饵速度，目标是让<b>鱼嘴</b>贴上饵）；
///    途中离出生点的距离超过「追逐距离」→ 放弃，或饵失效（不在水里/被收起）→ 放弃；
///  ▪ Returning（返回）——朝出生点附近游，回到后重新进入巡逻（回巢再战，无冷却）；
///  ▪ Bitten（咬中）——鱼嘴追到与饵距离 &lt; BiteRange → 直接判定咬中，鱼停住（等待钓上逻辑接管）。
///
/// 横版 2D 转向：**图片左边 = 鱼的前方**（未镜像的精灵图鱼头朝左）。左/右朝向用精灵的 Scale.X 符号表示：
/// 朝左 = +SpriteScale（不镜像）、朝右 = −SpriteScale（镜像）；每次左右翻转时 Scale.X 在 FishDef.TurnDuration
/// 秒内<b>线性</b>从一侧插到另一侧（经过 0 = 侧身压成一条线 = 转身动画），而不是瞬时 FlipH。
/// 平时自由游动鱼身水平；上钩后被拖行/收线时鱼身整体旋转到<b>运动方向</b>（鱼头指向运动方向），
/// 左右以竖直分界：运动方向 x &gt; 0（右上/右下）头朝右、x &lt; 0（左上/左下）头朝左、纯竖直沿用上一朝向。
///
/// 动画：prefab 里带一个 AnimatedSprite2D 子节点，_Ready 把 FishDef.SpriteFrames 赋给它并开播；
/// 播放速度（SpeedScale）由 Def 的 NormalPlaySpeed / StrugglePlaySpeed / ExhaustedPlaySpeed 三档决定，
/// 平时自由游动恒为 Normal，上钩后由 Hook 经 ApplyPlayback 切档。没挂 AnimatedSprite2D（如代码 new Fish()）
/// → 无动画纯逻辑（不可见但不崩）。
///
/// 依赖注入（与 Hook 同风格，便于测试）：
///  ▪ Water（Area2D）：水面/水底/左右岸从它读——挂 WaterBody 时用烘焙好的湖床折线几何，
///    普通矩形（未旋转 RectangleShape2D）退化为平水底；深度是「相对水面」的量。
///    行为策略可经 FishContext 读 DistanceToSurface / DistanceToBottom / DistanceToLeft / DistanceToRight / IsInWater，
///    余量（例如「离水面 100px 就不能再往上」）由策略自己判断，本类不内置；
///  ▪ BaitSource（Node2D）：饵的载体（场景里把 Hook 拖进来；测试可放任意 Node2D）。
/// 行为参数：BehaviorStrategy 挂内置 ClassicBehavior 时，巡逻/追饵参数直接写在策略实例上
/// （7 个导出字段），每帧经 ctx.BehaviorData 打包成 FishBehaviorData 写进鱼；裸鱼（没挂策略）用
/// Unconfigured 兜底（等同旧「行为数据为空」：横向范围取当前水深区间宽度），随便摆一条鱼也能动；
/// 不挂任何策略 = 鱼不动只漂着（生成时打一条警告）。
///
/// 每帧决策的出口：BehaviorStrategy（平时行为策略）——挂 ClassicBehavior = 内置数据驱动
/// 巡逻/追饵/返回（数据 = 策略自带参数，经 ctx.BehaviorData 流动）；挂自定义策略 = 该鱼种专属行为；
/// null = 不动。移动/翻转/钳制等「走路」机制始终留在本类。
///
/// 运动自包含：_PhysicsProcess 每物理帧推进；自动化测试把 ProcessMode 设 Disabled 后手动 Step(delta) 驱动。
/// </summary>
public sealed partial class Fish : Area2D
{
	/// <summary>鱼嘴追到距饵这个距离（px）以内 → 直接判定咬中（判定锚点是鱼嘴，不是鱼身中心）。</summary>
	[Export] public float BiteRange = 16f;

	/// <summary>巡逻带离水体边界的微量内缩（px）：避免目标/钳制点正好压在水底折线上
	/// （WaterSpace.IsWater 是严格内部判定，压线会被当成「不在水里」）。</summary>
	private const float EdgeInset = 1f;

	/// <summary>回到出生点附近（距离 px）即视为返回完成 → 重新进入巡逻。</summary>
	[Export] public float ReturnHomeDistance = 24f;

	/// <summary>_Ready 时是否自动刷到「巡逻范围 + 深度区间」内的随机刷新点（默认 true：场景里摆的鱼会自动就位）。</summary>
	[Export] public bool SpawnAtReady = true;

	/// <summary>平时行为策略（FishBehaviorBase）：巡逻/追饵/返回的每帧决策由它接管；null = 鱼不动，只漂着。
	/// 常规用法是留空由 FishSpawner 从 Def.BehaviorStrategy 注入；直接摆场景时也可手动挂（参考 Def 字段）。</summary>
	[Export] public FishBehaviorBase? BehaviorStrategy;

	/// <summary>行为数据源（运行时，非导出）：巡逻矩形/深度/感应/追逐距离读它。ClassicBehavior 每帧把
	/// 自己的 7 个导出参数打包写回（数据 → 数据流）；FishSpawner 生成时也从 (BehaviorStrategy as ClassicBehavior)
	/// 预填一份，让出生锚点/SpawnAtRandom 在挂上策略之前就能用同一份数据。
	/// 未配置 = Unconfigured（横向范围取当前水深区间宽度，等同旧「行为数据为空」的兜底）。</summary>
	internal FishBehaviorData BehaviorData = FishBehaviorData.Unconfigured;

	/// <summary>感应饵的水域（水面位置据此读）；null 时深度退化为以出生点为水面的相对量。</summary>
	[Export] public Area2D? Water;

	/// <summary>饵的载体（钩子）：用它的全局位置当饵的位置；null = 这条鱼没有可感应的饵。</summary>
	[Export] public Node2D? BaitSource;

	/// <summary>本鱼的鱼种定义（上钩后的体力/耐力最大值/挣扎拖速、钓上后的背包形态都从它读）；null = 未指定（上钩用通用默认值、钓上无物品）。</summary>
	[Export] public FishDef? Def;

	public enum State { Patrol, Chase, Returning, Bitten }

	/// <summary>当前状态。</summary>
	public State Current { get; private set; } = State.Patrol;

	/// <summary>出生点（巡逻/追逐的锚）：_Ready 记录部署位置；巡逻范围和追逐距离都相对它。</summary>
	public Vector2 SpawnPoint { get; private set; }

	/// <summary>生成时的初始深度（px，水面往下为正）：FishSpawner 在鱼就位后写入。
	/// 图鉴探索记录见它（FishEncyclopediaLog.See 读它），而不是移动中的实时深度——
	/// 避免鱼追饵/收线等位置偏移干扰「玩家探索到哪个深度」的判定。直接手工摆的鱼默认 0。</summary>
	public float SpawnDepth { get; set; }

	/// <summary>当前（逻辑）朝向是否朝左：朝左 = 精灵不镜像（Scale.X = +SpriteScale）、朝右 = 镜像（Scale.X = −SpriteScale）。
	/// 只在移动/拖行时更新；这个值在转身动画开始的瞬间就切换（视觉的 Scale.X 在 TurnDuration 内线性跟上）。</summary>
	public bool FacingLeft { get; private set; }

	/// <summary>当前视觉镜像系数：+1 = 朝左（不镜像）、−1 = 朝右（镜像）。转身过程中在两者间线性插值，
	/// 上来就对齐 FacingLeft；鱼嘴偏移也用它 —— 转身时嘴跟着身体一起扫过去，不会被瞬间翻到另一侧。</summary>
	private float _facingSign = -1f;

	/// <summary>本次转身的起始/目标镜像系数与已用时间、是否正在转身。</summary>
	private float _turnFromSign = -1f;
	private float _turnToSign = -1f;
	private float _turnElapsed;
	private bool _turning;

	/// <summary>上钩后被拖行时鱼身的世界旋转（让鱼头指向运动方向）；自由游动恒为 0（鱼身水平）。</summary>
	private float _faceAngle;

	/// <summary>MouthOffset 的「未配置」哨兵：只有精确等于这个值才当没设置。负分量本身是合法坐标
	/// （例如嘴偏出图片左边缘 1px = (-1,4)），不能再用「任一负分量」判断。</summary>
	private static readonly Vector2 UnconfiguredMouth = new(-1f, -1f);

	/// <summary>转身时长（秒）：读鱼种定义（FishDef.TurnDuration，默认 0.2）；无 Def 时也用 0.2。</summary>
	private float TurnDuration => Def?.TurnDuration ?? 0.2f;

	/// <summary>鱼嘴相对鱼中心的世界偏移：MouthOffset 是相对图片左上角(0,0)的像素坐标，减去图片中心
	/// （_spritePixelSize/2）得相对鱼中心的偏移，再 × SpriteScale、X 按<b>当前视觉朝向</b>镜像
	/// （图片左边=前方：朝左不镜像、朝右取反；转身动画中为插值值），最后随鱼身世界旋转一起转
	/// （上钩被拖行时鱼身倾斜，嘴偏移也要跟着倾斜）。
	/// MouthOffset = 默认哨兵 (-1,-1) → 嘴 = 鱼中心（偏移 0，旧行为）；其余值一律照用。</summary>
	public Vector2 MouthWorldOffset => MouthOffsetForSign(_facingSign).Rotated(GlobalRotation);

	/// <summary>指定朝向下的鱼嘴偏移（未旋转；自由游动鱼身水平时等于世界偏移）。
	/// 追饵选朝向/修正目标用离散值（转身动画中途不参与决策，保证移动轨迹稳定）。</summary>
	internal Vector2 MouthOffsetFor(bool facingLeft) => MouthOffsetForSign(facingLeft ? 1f : -1f);

	/// <summary>给定镜像系数（+1 朝左 / −1 朝右，可插值）下的鱼嘴本地偏移（未旋转）。</summary>
	private Vector2 MouthOffsetForSign(float sign)
	{
		var def = Def;
		if (def == null) return Vector2.Zero;
		var m = def.MouthOffset;
		if (m.X == UnconfiguredMouth.X && m.Y == UnconfiguredMouth.Y) return Vector2.Zero;   // 未配置 → 嘴 = 鱼中心
		var rel = m - _spritePixelSize * 0.5f;   // 左上角坐标 → 相对鱼中心
		return new Vector2(rel.X * sign * def.SpriteScale, rel.Y * def.SpriteScale);
	}

	/// <summary>鱼嘴世界坐标 = 鱼中心 + MouthWorldOffset（嘴偏移含朝向镜像与鱼身旋转）。咬钩判定 / 上钩挂载的锚点。</summary>
	public Vector2 MouthGlobalPosition => GlobalPosition + MouthWorldOffset;

	/// <summary>咬中（进入 Bitten）时触发：等待钓上/判定逻辑接管。</summary>
	public event Action? Bitten;

	/// <summary>让自动化测试用固定随机序列（默认不调用 = 引擎自动随机，真机无需可重现）。</summary>
	public void SeedRandom(ulong seed) => _rng.Seed = seed;

	private readonly RandomNumberGenerator _rng = new();
	private Area2D? _water;
	private Node2D? _bait;
	private WaterSpace? _space;        // 水几何缓存（Water 挂 WaterBody 时含湖床折线；普通矩形则退化为平水底）
	private bool _spaceResolved;
	private Vector2 _target;      // 当前巡逻目标点
	private AnimatedSprite2D? _sprite;   // 鱼的动画精灵（prefab 的 AnimatedSprite2D 子节点；null = 无动画纯逻辑）
	private Vector2 _spritePixelSize;    // 精灵图参考尺寸（最大帧宽/高，像素）：碰撞体与鱼嘴锚点共用；无精灵 = (0,0)
	
	public override void _Ready()
	{
		SpawnPoint = GlobalPosition;   // 部署位置即出生点锚
		_water = Water;
		_bait = BaitSource;
		_space = null;
		_spaceResolved = false;
		if (SpawnAtReady) SpawnAtRandom();
		SetupSprite();
		if (BehaviorStrategy == null)
			GD.PushWarning($"[Fish] 这尾鱼（{Def?.DisplayName ?? Name}）没有行为策略（BehaviorStrategy 为空）：鱼不动，只在水里漂着。");
	}


	/// <summary>解析并装配动画精灵：把 Def 的精灵表赋给 AnimatedSprite2D 并开播，初始速度 = 正常播放速度；
	/// 显示缩放 = SpriteScale，碰撞体按最终精灵表自动适配。prefab 带 AnimatedSprite2D；代码 new Fish() 无此子节点 → 无动画纯逻辑（不可见但不崩）。</summary>
	private void SetupSprite()
	{
		_sprite = GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D");
		if (_sprite == null) return;
		var frames = Def?.SpriteFrames;
		if (frames != null)
		{
			_sprite.SpriteFrames = frames;
			var names = frames.GetAnimationNames();
			if (names.Length > 0) _sprite.Play(names[0]);   // 播第一个动画（精灵表默认动画）
			_spritePixelSize = SpritePixelSize(frames);    // 精灵图参考尺寸（最大帧宽/高，像素）
		}
		_sprite.SpeedScale = Def?.NormalPlaySpeed ?? 1f;

		// 初始朝向 = FacingLeft（默认 false = 朝右 → 镜像/Scale.X 取负）；转身过渡从当前侧开始
		_facingSign = FacingLeft ? 1f : -1f;
		_turnFromSign = _facingSign;
		_turnToSign = _facingSign;
		_turning = false;
		ApplySpriteFacing();

		FitCollisionToSprite(Def?.SpriteScale ?? 6f);   // 碰撞体按最终显示的精灵表自动适配
	}

	/// <summary>碰撞体按精灵表自动适配：用 _spritePixelSize（最大帧宽/高）乘 SpriteScale 得世界尺寸，
	/// 赋给 CapsuleShape2D。prefab 里 CollisionShape2D 已旋转 90° → 水平胶囊：Height = 鱼身长（水平）、
	/// Radius = 半厚（鱼身高的一半）。没有精灵表（尺寸为 0）时不动作。</summary>
	private void FitCollisionToSprite(float scale)
	{
		if (_spritePixelSize.X <= 0f || _spritePixelSize.Y <= 0f) return;
		if (GetNodeOrNull<CollisionShape2D>("CollisionShape2D") is not { Shape: CapsuleShape2D capsule })
			return;

		capsule.Height = _spritePixelSize.X * scale;         // 水平总长（鱼身长）
		capsule.Radius = _spritePixelSize.Y * scale * 0.5f;  // 竖直半厚（鱼身高的一半）
	}

	/// <summary>精灵图参考尺寸（像素）= 所有动画所有帧的最大宽/高。碰撞体与鱼嘴锚点都用它当「原图」基准
	/// （SpriteFrames 各帧通常同尺寸；不一致时取最大，保证碰撞/嘴锚点用同一参考）。</summary>
	private static Vector2 SpritePixelSize(SpriteFrames frames)
	{
		var maxW = 0;
		var maxH = 0;
		foreach (var anim in frames.GetAnimationNames())
		{
			for (var i = 0; i < frames.GetFrameCount(anim); i++)
			{
				var tex = frames.GetFrameTexture(anim, i);
				if (tex == null) continue;
				maxW = Mathf.Max(maxW, tex.GetWidth());
				maxH = Mathf.Max(maxH, tex.GetHeight());
			}
		}
		return new Vector2(maxW, maxH);
	}

	/// <summary>把这条鱼刷到「出生点 ± 巡逻范围 × 水面下深度区间」内的随机刷新点（刷新位置由行为数据推导）。</summary>
	public void SpawnAtRandom()
	{
		if (BehaviorData.PatrolDistanceX <= 0f) return;   // 未配置横向范围 → 不刷（等同旧「行为数据为空」）
		GlobalPosition = RandomPointIn();
		Current = State.Patrol;
		PickNextTarget();
	}

	public override void _PhysicsProcess(double delta) => Step(delta);

	/// <summary>推进一帧：先推进转身动画（Scale.X 线性过渡），再把决策委托给 BehaviorStrategy
	/// （平时行为策略；null = 不动只漂着——空槽语义）。挂 ClassicBehavior 即恢复内置数据驱动巡逻/追饵/返回。
	/// 「走路」机制（左右朝向、转身动画、巡逻钳制）始终在本类。</summary>
	public void Step(double delta)
	{
		AdvanceTurn((float)delta);   // 转身过渡（视觉 Scale.X 线性插值）每帧推进，与行为/上钩状态无关
		if (BehaviorStrategy != null)
		{
			var ctx = new FishContext(this);
			BehaviorStrategy.Update(ctx, delta);
		}
	}

	// ---- 供 FishContext 调用的决策原语（内置状态机与此共用同一套「走路」规则） ----

	/// <summary>切换当前状态（供行为实现 / 内置默认调用）。</summary>
	internal void ChangeState(State state) => Current = state;

	/// <summary>触发咬中事件（行为实现判定贴脸后调用）。</summary>
	internal void NotifyBitten() => Bitten?.Invoke();

	/// <summary>当前巡逻目标点的内部视图（给行为上下文的只读 CurrentTarget 用）。</summary>
	internal Vector2 CurrentTargetInternal => _target;

	internal void PickNextTarget() => _target = RandomPointIn();

	/// <summary>朝目标以给定速度移动（步长截断避免过冲；patrolClamp 时钳在巡逻矩形内）。
	/// 移动被水面/岸/水底挡住时，先退到边界再沿边界切线滑掉剩余距离，不会「顶着头撞」。
	/// 默认按实际水平位移更新朝向（自由游动/巡逻）；追饵那种「目标已按鱼嘴修正过」的移动传 false，保持既定朝向。</summary>
	internal void MoveToward(Vector2 target, float speed, float dt, bool patrolClamp, bool updateFacing = true)
	{
		var desired = target - GlobalPosition;
		var dist = desired.Length();
		if (dist < 0.001f) return;
		var step = Mathf.Min(speed * dt, dist);
		var move = MoveInsideWater(desired / dist, step);
		if (updateFacing && Mathf.Abs(move.X) > 0.001f)   // 只要有水平位移就更新朝向（不能用 >1f：低速巡逻每帧水平位移 ≤1px 会永不翻转）
			SetFacingLeft(move.X < 0f);

		var next = GlobalPosition + move;
		if (patrolClamp) next = ClampToPatrolRect(next);   // 巡逻严格不出矩形；追饵/返回放开
		GlobalPosition = next;
	}

	/// <summary>让<b>鱼嘴</b>（而不是鱼身中心）朝目标靠近：先在两个朝向里选「嘴离目标更近」的那个，
	/// 再把移动目标修正为 target − 该朝向的嘴偏移（这样步长走到时嘴正好落在目标上）。
	/// 追饵/追钩用这个；普通巡逻仍用 MoveToward 走中心。</summary>
	internal void MoveMouthToward(Vector2 target, float speed, float dt, bool patrolClamp)
	{
		var leftMouth = GlobalPosition + MouthOffsetFor(true);
		var rightMouth = GlobalPosition + MouthOffsetFor(false);
		var leftDist = leftMouth.DistanceSquaredTo(target);
		var rightDist = rightMouth.DistanceSquaredTo(target);
		// 两个朝向的嘴位置重合（未配置 / 嘴在图片水平中心）或到目标等距 → 无法用嘴区分，按目标在左/右定朝向
		var faceLeft = Mathf.Abs(leftDist - rightDist) < 0.0001f
			? (Mathf.Abs(target.X - GlobalPosition.X) > 0.001f ? target.X < GlobalPosition.X : FacingLeft)
			: leftDist < rightDist;
		SetFacingLeft(faceLeft);
		MoveToward(target - MouthOffsetFor(faceLeft), speed, dt, patrolClamp, updateFacing: false);
	}

	/// <summary>设置朝向（true = 朝左）。图片左边=前方：朝左 = 精灵不镜像（Scale.X 正）、朝右 = 镜像（Scale.X 负）。
	/// 朝向真正翻转时先起一段转身动画：FacingLeft 立即更新（逻辑/嘴偏移计算马上生效），
	/// 视觉的 Scale.X 在 FishDef.TurnDuration 内线性从旧侧插到新侧（Web 版本：Scale.X 从 1 线性到 −1）。
	/// 同一朝向重复调用不会重启动画；TurnDuration = 0 或无精灵（纯逻辑鱼）时瞬时切换。</summary>
	public void SetFacingLeft(bool left)
	{
		FacingLeft = left;
		var target = left ? 1f : -1f;

		if (_sprite == null || TurnDuration <= 0f)   // 无精灵 / 时长为 0 → 瞬时翻转（旧行为）
		{
			_facingSign = target;
			_turnToSign = target;
			_turning = false;
			ApplySpriteFacing();
			return;
		}

		if (Mathf.Abs(_turnToSign - target) < 0.0001f) return;   // 已经在该侧 / 正在往该侧转 → 不重启

		_turnFromSign = _facingSign;   // 从当前视觉位置起转（转身中途改主意也能接上）
		_turnToSign = target;
		_turnElapsed = 0f;
		_turning = true;
	}

	/// <summary>按给定方向的水平分量重算朝向（|x| 过小 = 方向几乎垂直 → 保持原朝向）。
	/// 只改左右朝向（水平），不倾斜鱼身；上钩瞬间由 Hook 调用（此时还没移动，鱼身保持水平）。</summary>
	public void FaceAlong(Vector2 direction)
	{
		if (Mathf.Abs(direction.X) > 0.001f) SetFacingLeft(direction.X < 0f);
	}

	/// <summary>上钩后被拖行/收线时用：把鱼身整体转到运动方向，让<b>鱼头指向运动方向</b>；
	/// 左右以<b>竖直分界</b>——运动方向 x &gt; 0（右上/右下）→ 头朝右，x &lt; 0（左上/左下）→ 头朝左，
	/// |x| ≈ 0（纯竖直）→ 沿用当前左右。旋转角按「图片左边=前方」推导：
	/// 朝左（不镜像）头 = 局部 −X → θ = atan2(−d.Y, −d.X)；朝右（镜像）头 = 局部 +X → θ = atan2(d.Y, d.X)。
	/// 两种情况 |θ| 都 ≤ 90°：鱼只会斜着游，不会肚皮朝天。倾斜角变化即时生效（不算转身，不做过渡）。
	/// 左右翻转走 TurnDuration 的转身动画（由 SetFacingLeft 启动）。</summary>
	public void FaceMovement(Vector2 direction)
	{
		if (direction.LengthSquared() < 1e-6f) return;
		if (Mathf.Abs(direction.X) > 0.001f) SetFacingLeft(direction.X < 0f);   // 竖直分界：右半平面头朝右、左半平面头朝左

		_faceAngle = FacingLeft
			? Mathf.Atan2(-direction.Y, -direction.X)   // 不镜像：鱼头 = 局部 −X，世界方向 = −(cos θ, sin θ)
			: Mathf.Atan2(direction.Y, direction.X);    // 镜像：鱼头 = 局部 +X，世界方向 = (cos θ, sin θ)
	}

	/// <summary>推进转身动画：Scale.X 的镜像系数从起始值<b>线性</b>插到目标值（经过 0 = 侧身压成一条竖线）。
	/// 自由游动时由 Fish.Step 每帧调用；上钩后由 Fish.StepHooked（Hook 每帧驱动）调用。</summary>
	private void AdvanceTurn(float dt)
	{
		if (!_turning) return;

		var duration = TurnDuration;
		if (duration <= 0f)
		{
			_facingSign = _turnToSign;
			_turning = false;
			ApplySpriteFacing();
			return;
		}

		_turnElapsed += dt;
		var t = Mathf.Clamp(_turnElapsed / duration, 0f, 1f);
		_facingSign = Mathf.Lerp(_turnFromSign, _turnToSign, t);   // 线性（不取缓动）：Scale.X 匀速从 +S 扫到 −S
		if (t >= 1f)
		{
			_facingSign = _turnToSign;
			_turning = false;
		}
		ApplySpriteFacing();
	}

	/// <summary>把镜像系数落到精灵上：FlipH 恒 false，朝向统一用 Scale.X 的符号（负 = 镜像）——
	/// 这样转身时才能让 Scale.X 在两个符号之间连续插值。</summary>
	private void ApplySpriteFacing()
	{
		if (_sprite == null) return;
		var scale = Def?.SpriteScale ?? 6f;
		_sprite.FlipH = false;
		_sprite.Scale = new Vector2(_facingSign * scale, scale);
	}

	/// <summary>把鱼嘴钉到世界坐标 worldPos 上：先把鱼身旋转到 _faceAngle（上钩后被拖行时 = 运动方向；
	/// 未设置 = 0，鱼身水平），鱼中心 = worldPos − 当前朝向的嘴偏移（嘴偏移随鱼身旋转一起转）。
	/// 上钩后每帧由 Hook 调用（钩子会移动/旋转）；显式设置「世界旋转」而不是继承钩子自身的转向。</summary>
	internal void SnapMouthTo(Vector2 worldPos)
	{
		GlobalRotation = _faceAngle;
		GlobalPosition = worldPos - MouthWorldOffset;
	}

	/// <summary>上钩后由 Hook 每帧调用（替代自由游动的 Step）：推进转身动画 → 按钩子本帧移动方向把鱼身
	/// 转到运动方向（|v| ≤ 1 px/s 视为没动，保持当前朝向与倾角）→ 把鱼嘴钉到钩子上。</summary>
	internal void StepHooked(float dt, Vector2 moveDir, Vector2 hookPos)
	{
		AdvanceTurn(dt);
		if (moveDir.LengthSquared() > 1f) FaceMovement(moveDir);
		SnapMouthTo(hookPos);
	}

	/// <summary>沿 dir 走最多 step：无阻挡 → 直线；撞边界 → 走到边界，再沿边界切线滑掉剩余步长。</summary>
	private Vector2 MoveInsideWater(Vector2 dir, float step)
	{
		var space = ResolveSpace();
		if (space == null) return dir * step;
		return space.MoveInside(GlobalPosition, dir * step);
	}

	/// <summary>把点钳制在「出生点 ± 左右巡逻范围 × 水面下深度区间」的矩形内（横版：X 有界、Y 在深度区间）。
	/// 横向再与水体左右岸取交集，保证巡逻路径不游出水面。</summary>
	private Vector2 ClampToPatrolRect(Vector2 p)
	{
		var (xLo, xHi) = PatrolXRange();
		var (minY, maxY) = DepthRangeY();
		return new Vector2(Mathf.Clamp(p.X, xLo, xHi), Mathf.Clamp(p.Y, minY, maxY));
	}

	/// <summary>在巡逻矩形里随机取一点（SpawnPoint 为锚：X 在出生点 ± 巡逻范围，Y 在深度区间）。</summary>
	internal Vector2 RandomPointIn()
	{
		var (xLo, xHi) = PatrolXRange();
		var (minY, maxY) = DepthRangeY();
		return new Vector2(_rng.RandfRange(xLo, xHi), _rng.RandfRange(minY, maxY));
	}

	/// <summary>巡逻 x 带 = 出生点 ± 巡逻范围，再与水体左右岸取交集。</summary>
	private (float Min, float Max) PatrolXRange()
	{
		var range = PatrolRangeX();
		var lo = Mathf.Min(SpawnPoint.X - range, SpawnPoint.X + range);
		var hi = Mathf.Max(SpawnPoint.X - range, SpawnPoint.X + range);

		var space = ResolveSpace();
		if (space != null)
		{
			lo = Mathf.Max(lo, space.XMin);
			hi = Mathf.Min(hi, space.XMax - EdgeInset);
			if (hi < lo) hi = lo;
		}
		return (lo, hi);
	}

	/// <summary>左右巡逻范围（px）：出生点 X ± 此值。行为数据缺省用「水区宽度的一半」当横向范围（内置默认也能游起来）。</summary>
	internal float PatrolRangeX() => BehaviorData.PatrolDistanceX > 0f ? BehaviorData.PatrolDistanceX : WaterRectDepthSpan();

	/// <summary>
	/// 巡视矩形 y 区间 = 水面 + [SwimDepthMin, SwimDepthMax]，且整段巡逻 x 范围内都不低于水底
	/// （取该范围水底的最小 y = 最浅处，保证左右游动时不会穿底）。
	/// 有 WaterSpace 时水面/水底读湖床几何；否则回退到 Water 矩形的上下边；都没有 → 以出生点当水面。
	/// </summary>
	private (float MinY, float MaxY) DepthRangeY()
	{
		var dMin = BehaviorData.SwimDepthMin;
		var dMax = BehaviorData.SwimDepthMax;
		var space = ResolveSpace();

		float top;
		float floor;
		if (space != null)
		{
			top = space.SurfaceY;
			var (xLo, xHi) = PatrolXRange();
			floor = space.FloorMinY(xLo, xHi) - EdgeInset;   // 整段巡逻 x 范围的最浅水底，再内缩一点
		}
		else
		{
			var rect = WaterBody.RectOf(_water);
			top = rect?.Position.Y ?? SpawnPoint.Y;               // 无水域 → 以出生点为水面
			floor = rect.HasValue ? rect.Value.Position.Y + rect.Value.Size.Y : float.PositiveInfinity;
		}

		var maxY = Mathf.Max(Mathf.Min(top + dMax, floor), top);    // 不穿水底（也不能跑到水面以上）
		var minY = Mathf.Min(Mathf.Max(top + dMin, top), maxY);
		return (minY, maxY);
	}

	internal float SenseRange() => BehaviorData.SenseRadius;
	internal float ChaseRange() => BehaviorData.ChaseRange;

	/// <summary>水的下边界宽度（px）——行为缺省时当巡逻横向范围用（水区宽度的一半，默认小鱼在水域中间游）。</summary>
	private float WaterRectDepthSpan()
	{
		var rect = WaterBody.RectOf(_water);
		return rect.HasValue ? rect.Value.Size.X * 0.5f : 200f;
	}

	// ---- 饵位置：饵必须在「水里」才算有效（收线收起/钩子离水的饵 鱼不感应）；无水域时认为在水里 ----

	internal Vector2? BaitPosition()
	{
		if (_bait == null || !GodotObject.IsInstanceValid(_bait)) return null;
		var p = _bait.GlobalPosition;
		var space = ResolveSpace();
		if (space != null && !space.IsWater(p)) return null;
		return p;
	}

	/// <summary>指定水域（也可直接赋值 Water，两者等价；测试用注入避免场景依赖）。</summary>
	public void ConnectWater(Area2D? water)
	{
		_water = water;
		_space = null;
		_spaceResolved = false;
	}

	/// <summary>指定饵载体（也可直接赋值 BaitSource）。</summary>
	public void ConnectBait(Node2D? bait) => _bait = bait;

	/// <summary>上钩后由 Hook 每帧调用：按播放状态切 AnimatedSprite2D 的 SpeedScale
	/// （Normal → NormalPlaySpeed / Struggle → StrugglePlaySpeed / Exhausted → ExhaustedPlaySpeed）。
	/// 平时自由游动恒为 Normal（_Ready 已设好），无需调用。</summary>
	internal void ApplyPlayback(FishPlayback state)
	{
		if (_sprite == null) return;
		_sprite.SpeedScale = state switch
		{
			FishPlayback.Struggle => Def?.StrugglePlaySpeed ?? 1f,
			FishPlayback.Exhausted => Def?.ExhaustedPlaySpeed ?? 1f,
			_ => Def?.NormalPlaySpeed ?? 1f,
		};
	}

	// ---- 水几何：首次查询时解析并缓存（Water 挂 WaterBody → 湖床几何；普通矩形 → 平水底） ----

	/// <summary>本鱼所在水体的几何（供行为上下文读距离/做探测）；没有可用水域 → null。
	/// Water 挂 WaterBody 时每帧直读它的 Space（地形 Rebuild/SetSpace 后自动生效，读的是同一个字段，开销 O(1)）；
	/// 普通 Area2D 矩形只在首次查询时解析一次（场景里不会变）。</summary>
	internal WaterSpace? ResolveSpace()
	{
		if (_water is WaterBody body)
		{
			_space = body.Space;
			_spaceResolved = true;
			return _space;
		}
		if (_spaceResolved) return _space;
		_spaceResolved = true;
		_space = WaterBody.SpaceOf(_water);
		return _space;
	}
}

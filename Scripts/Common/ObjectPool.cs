using System;
using System.Collections.Generic;
using Godot;

namespace SaleFish.Common;

/// <summary>
/// 对象池（Godot 版）。从 Unity 单例搬过来改写，语义一一对应：
///  ▪ 全局单例：挂成 Godot autoload（见 project.godot），静态 ObjectPool.Instance 访问 —— 等价 Unity 的
///    Singleton_Global + DontDestroyOnLoad（autoload 跨场景存活）；
///  ▪ 实例化：Godot 没有 Instantiate(GameObject) —— 用「创建工厂委托」现造，或传 PackedScene 走场景实例化；
///  ▪ 停用/复用：Godot 没有 GameObject.SetActive —— “回收” = 把节点**从场景树上摘下来**（不在树里就
///    不渲染、不处理），等效 Unity 的 SetActive(false)；对象只由池的队列持有（无需养在池根下）。
///    再取用时由调用方 AddChild 挂回任意父节点即回到场景；
///  ▪ 归池识别：不再需要 PooledIdentifier 组件 —— 节点上记来源 key 的 metadata，Return 时自动识别归哪个池。
///
/// 注意：ProcessMode.Disabled 只停处理不停渲染，不能当「隐藏/停用」用——停用必须摘树（本池的 Return 已做）。
///
/// 池以 key 区分（同一 key 的对象才能互相复用），key 用 GodotObject
/// （Resource / PackedScene / Node…，内部按实例 Id 记账）；非池来源的对象 Return 时直接释放。
/// </summary>
public sealed partial class ObjectPool : Node
{
    /// <summary>autoload 注入的全局实例（project.godot 里挂的 ObjectPool 节点）。</summary>
    public static ObjectPool Instance { get; private set; } = null!;

    /// <summary>节点 metadata 里记来源 key 的字段名。</summary>
    private const string PoolKeyMeta = "_objectpool_key_id";

    private readonly Dictionary<ulong, Queue<Node>> _pools = [];

    public override void _Ready()
    {
        Instance = this;
    }

    /// <summary>退出关停时释放全部空闲对象：池里是离场节点，引擎不会自动回收，不释放会泄漏。</summary>
    public override void _ExitTree()
    {
        foreach (var pool in _pools.Values)
            while (pool.Count > 0)
            {
                var node = pool.Dequeue();
                if (IsInstanceValid(node)) node.Free();
            }
        _pools.Clear();
    }

    /// <summary>取一个对象：池中有该 key 的空闲对象则复用，没有则用 create 现造。
    /// 返回对象已恢复处理状态、且**不在场景树里**（Godot 4 的 AddChild 不会自动改挂父节点），
    /// 由调用方 AddChild 到目标父节点即可使用。</summary>
    public T Get<T>(GodotObject key, Func<T> create) where T : Node
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);

        T node;
        if (_pools.TryGetValue(key.GetInstanceId(), out var pool) && pool.Count > 0)
            node = (T)pool.Dequeue();
        else
            node = create();

        node.ProcessMode = ProcessModeEnum.Inherit;
        node.SetMeta(PoolKeyMeta, key.GetInstanceId());   // 记住来源，Return 时据此归池
        return node;
    }

    /// <summary>便捷版：key 直接用 PackedScene（场景即 Unity 的 prefab），省写工厂。</summary>
    public Node Get(PackedScene scene) => Get(scene, () => scene.Instantiate());

    /// <summary>回收对象到它来源的池：**摘树离场**（不在场景树 → 不渲染不处理）待复用。
    /// 不是从池里取出的对象（无来源标记）会直接释放并告警。</summary>
    public void Return(Node node)
    {
        if (node == null || !IsInstanceValid(node)) return;
        if (!node.HasMeta(PoolKeyMeta))
        {
            GD.PushWarning($"[ObjectPool] {node.Name} 不是从对象池取出的，改为直接释放。");
            node.QueueFree();
            return;
        }

        var keyId = (ulong)node.GetMeta(PoolKeyMeta);
        if (!_pools.TryGetValue(keyId, out var pool))
        {
            pool = new Queue<Node>();
            _pools[keyId] = pool;
        }

        node.GetParent()?.RemoveChild(node);   // 摘树即「停用」：不渲染、不处理，等效 Unity SetActive(false)
        node.ProcessMode = ProcessModeEnum.Disabled;
        pool.Enqueue(node);
    }

    /// <summary>预创建 count 个对象放入池中（现造，先不入场景树），后续 Get 直接复用、不再实例化。</summary>
    public void Preload<T>(GodotObject key, Func<T> create, int count) where T : Node
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);
        if (count <= 0) return;

        var keyId = key.GetInstanceId();
        for (var i = 0; i < count; i++)
        {
            var node = create();
            node.SetMeta(PoolKeyMeta, keyId);
            node.ProcessMode = ProcessModeEnum.Disabled;
            if (!_pools.TryGetValue(keyId, out var pool))
            {
                pool = new Queue<Node>();
                _pools[keyId] = pool;
            }
            pool.Enqueue(node);
        }
    }

    /// <summary>释放某个 key 的全部空闲对象（清池）。池外的在用对象不受影响。</summary>
    public void Clear(GodotObject key)
    {
        if (key == null) return;
        if (!_pools.TryGetValue(key.GetInstanceId(), out var pool)) return;
        while (pool.Count > 0)
        {
            var node = pool.Dequeue();
            if (IsInstanceValid(node)) node.QueueFree();
        }
        _pools.Remove(key.GetInstanceId());
    }
}
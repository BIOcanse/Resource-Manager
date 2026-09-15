namespace ResourceManager.App.Domain.Messages;

/// <summary>
/// 消息域。每个域内的码从 1 开始，0 保留为「未指定」。
/// 域和码一旦发布就不改含义；废弃的码留空位，不复用。
/// </summary>
public static class BackendMessageDomains
{
    public const byte Unspecified = 0;

    /// <summary>可选依赖与组件（安装位置、可否清理等）。</summary>
    public const byte Dependency = 1;

    /// <summary>GPU 放置能力判定。</summary>
    public const byte GpuPlacement = 2;

    /// <summary>指标目录里某个指标为什么不能选。</summary>
    public const byte Metric = 3;
}

/// <summary>
/// 各域的消息码。参数个数写在注释里，前端文案按同样的顺序取参数。
/// </summary>
public static class BackendMessageCodes
{
    public static class Dependency
    {
        /// <summary>该依赖装在我们自己的受管 Dependencies 根目录里，可以清理。参数：无。</summary>
        public const byte ManagedRootRemovable = 1;

        /// <summary>该依赖当前没有可清理的受管安装内容。参数：无。</summary>
        public const byte ManagedRootEmpty = 2;

        /// <summary>分类标签：组件依赖。参数：无。</summary>
        public const byte CategoryTag = 3;

        /// <summary>操作名：卸载。参数：无。</summary>
        public const byte UninstallAction = 4;
    }

    public static class GpuPlacement
    {
        /// <summary>这台机器只有一个显卡，GPU 调度没有可选目标。参数：无。</summary>
        public const byte SingleAdapter = 1;

        /// <summary>设置里把 GPU 调度关掉了。参数：无。</summary>
        public const byte DisabledBySetting = 2;
    }
}

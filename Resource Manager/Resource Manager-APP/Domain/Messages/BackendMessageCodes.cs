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

    /// <summary>软件记录（卸载能力、来源说明等）。</summary>
    public const byte Software = 4;
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

        /// <summary>已安装在受管 Dependencies 根目录。参数：无。</summary>
        public const byte InstalledInManagedRoot = 5;

        /// <summary>安装器已在 Misc 缓存里，可以安装。参数：无。</summary>
        public const byte InstallerCached = 6;

        /// <summary>可从官方发布页下载，并且可以选版本。参数：无。</summary>
        public const byte DownloadableWithVersionChoice = 7;

        /// <summary>可从官方来源下载安装器。参数：无。</summary>
        public const byte Downloadable = 8;

        /// <summary>需要人工从来源页取安装器放进缓存目录。参数：无。</summary>
        public const byte ManualAcquisition = 9;

        /// <summary>检测到系统已有安装，直接复用。参数：1 = 安装目录。</summary>
        public const byte ReusingExternalInstall = 10;

        /// <summary>Provider 已通过实时指标验证。参数：无。</summary>
        public const byte ProviderVerified = 11;

        /// <summary>检测到系统已安装的 Provider 运行库，但还没有通过实时读数验证。参数：无。</summary>
        public const byte ProviderRuntimeUnverified = 12;

        /// <summary>组件文件已就绪，但 Provider 还没有通过实时读数验证。参数：无。</summary>
        public const byte ComponentFilesUnverified = 13;

        /// <summary>运行库可用，但 Provider 桥接或实时读数验证尚未完成。参数：无。</summary>
        public const byte RuntimeAvailableBridgePending = 14;

        /// <summary>Provider 桥接尚未接入，或当前硬件/驱动未返回可验证读数。参数：无。</summary>
        public const byte ProviderBridgeMissing = 15;

        /// <summary>内置组件已通过实时指标验证。参数：无。</summary>
        public const byte BundledVerified = 16;

        /// <summary>内置组件已安装；当前硬件或 OEM 运行库未返回可验证读数。参数：无。</summary>
        public const byte BundledUnverified = 17;

        /// <summary>组件已安装，直接复用现有安装。参数：无。</summary>
        public const byte AlreadyInstalledReuse = 18;

        /// <summary>安装器已下载到托管依赖目录。参数：无。</summary>
        public const byte InstallerDownloaded = 19;

        /// <summary>已下载指定版本的安装器到托管依赖目录。参数：1 = 版本。</summary>
        public const byte InstallerDownloadedVersion = 20;

        /// <summary>共享 WebView2 Runtime 已安装并通过检测。参数：无。</summary>
        public const byte SharedRuntimeInstalled = 21;

        /// <summary>安装器已可见启动，需要用户完成厂商安装提示。参数：无。</summary>
        public const byte InstallerLaunched = 22;
    }

    public static class Software
    {
        /// <summary>Windows 卸载注册表里有卸载入口。参数：无。</summary>
        public const byte HasUninstallEntry = 1;

        /// <summary>缺少卸载入口。参数：无。</summary>
        public const byte MissingUninstallEntry = 2;

        /// <summary>操作名：卸载。参数：无。</summary>
        public const byte UninstallAction = 3;

        /// <summary>操作名：不可卸载。参数：无。</summary>
        public const byte CannotUninstallAction = 4;

        /// <summary>启动 Windows 注册表提供的官方卸载器。参数：无。</summary>
        public const byte WindowsUninstallerDescription = 5;

        /// <summary>缺少卸载入口，只能跳转或手动处理，不删除未知软件根目录。参数：无。</summary>
        public const byte NoUninstallUnknownRoot = 6;

        /// <summary>手动分类/补录项不直接代表卸载器。参数：无。</summary>
        public const byte ManualClassificationNoUninstall = 7;

        /// <summary>受控软件卸载策略需要独立记录，当前只支持数据迁移恢复。参数：无。</summary>
        public const byte ControlledNoUninstall = 8;

        /// <summary>资源管理器自身不能从这里卸载或迁移。参数：无。</summary>
        public const byte SelfNoUninstall = 9;

        /// <summary>适配软件需要先在注册/manifest 里声明卸载策略。参数：无。</summary>
        public const byte AdaptedNoUninstall = 10;

        /// <summary>L0 受控注册没有卸载策略。参数：无。</summary>
        public const byte LegacyControlledNoUninstall = 11;

        /// <summary>便携软件没有卸载器，删除原文件后刷新会移除记录。参数：无。</summary>
        public const byte PortableNoUninstall = 12;
    }

    public static class Metric
    {
        /// <summary>当前硬件或 Provider 未暴露这个读数。参数：无。</summary>
        public const byte NotExposed = 1;

        /// <summary>Provider 未返回有效读数，需要更完整的组件或对应硬件。参数：1 = 组件名。</summary>
        public const byte NeedsComponent = 2;

        /// <summary>当前硬件或 Provider 未返回有效读数。参数：无。</summary>
        public const byte NoValidReading = 3;
    }

    public static class GpuPlacement
    {
        /// <summary>这台机器只有一个显卡，GPU 调度没有可选目标。参数：无。</summary>
        public const byte SingleAdapter = 1;

    }
}

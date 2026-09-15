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

    /// <summary>设备拓扑快照（说明、数据源失败、端口可信度与来源）。</summary>
    public const byte DeviceTopology = 5;
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

    /// <summary>
    /// 设备拓扑。只覆盖我们自己写的措辞：快照说明、数据源失败、端口的可信度与来源。
    /// Windows 或设备自报的名称（友好名、厂商、型号、EDID）不走这里，按原样透传。
    /// </summary>
    public static class DeviceTopology
    {
        /// <summary>拓扑来自 Windows 当前枚举，不代表机身物理位置。参数：无。</summary>
        public const byte EnumerationOnly = 1;

        /// <summary>本轮快照没有可展示的拓扑节点。参数：无。</summary>
        public const byte NoVisibleNodes = 2;

        /// <summary>同一设备返回了相互冲突的事实，本轮未发布。参数：1 = 设备 ID。</summary>
        public const byte ConflictingFacts = 3;

        /// <summary>SMBIOS / WMI 没有返回完整品牌型号。参数：无。</summary>
        public const byte IncompleteBrandModel = 4;

        /// <summary>USB 关系链不可用，只能显示设备枚举结果。参数：无。</summary>
        public const byte UsbChainUnavailable = 5;

        /// <summary>PnP 设备枚举失败。参数：1 = 原始错误。</summary>
        public const byte PnpEnumerationFailed = 6;

        /// <summary>SetupAPI / CfgMgr32 设备属性读取失败。参数：1 = 原始错误。</summary>
        public const byte NativeDevicePropertiesFailed = 7;

        /// <summary>USB Hub IOCTL 读取失败。参数：1 = 原始错误。</summary>
        public const byte UsbHubIoctlFailed = 8;

        /// <summary>网络接口属性读取失败。参数：1 = 原始错误。</summary>
        public const byte NetworkAdapterPropertiesFailed = 9;

        /// <summary>显示协调器尚未提供完整快照。参数：1 = 当前状态。</summary>
        public const byte DisplayCoordinatorNotReady = 10;

        /// <summary>显示协调器缓存读取失败。参数：1 = 原始错误。</summary>
        public const byte DisplayCoordinatorReadFailed = 11;

        /// <summary>磁盘/分区/卷能力读取不完整。参数：1 = 原始错误。</summary>
        public const byte StorageCapabilitiesIncomplete = 12;

        /// <summary>USB 控制器枚举失败。参数：1 = 原始错误。</summary>
        public const byte UsbControllerEnumerationFailed = 13;

        /// <summary>USB Hub 枚举失败。参数：1 = 原始错误。</summary>
        public const byte UsbHubEnumerationFailed = 14;

        /// <summary>USB 控制器关系链读取失败。参数：1 = 原始错误。</summary>
        public const byte UsbControllerRelationshipFailed = 15;

        /// <summary>可信度：USB Hub IOCTL + 设备管理器属性。参数：无。</summary>
        public const byte ConfidenceUsbHubIoctl = 16;

        /// <summary>可信度：WMI 关系链 + 设备管理器属性。参数：无。</summary>
        public const byte ConfidenceWmiChainDeviceManager = 17;

        /// <summary>可信度：WMI 关系链 + 设备管理器属性 + 名称推断。参数：无。</summary>
        public const byte ConfidenceWmiChainDeviceManagerNameInference = 18;

        /// <summary>可信度：WMI 关系链。参数：无。</summary>
        public const byte ConfidenceWmiChain = 19;

        /// <summary>可信度：WMI 关系链 + 名称推断。参数：无。</summary>
        public const byte ConfidenceWmiChainNameInference = 20;

        /// <summary>可信度：设备管理器属性。参数：无。</summary>
        public const byte ConfidenceDeviceManager = 21;

        /// <summary>可信度：设备管理器属性 + 名称推断。参数：无。</summary>
        public const byte ConfidenceDeviceManagerNameInference = 22;

        /// <summary>可信度：名称推断。参数：无。</summary>
        public const byte ConfidenceNameInference = 23;

        /// <summary>可信度：设备枚举。参数：无。</summary>
        public const byte ConfidenceDeviceEnumeration = 24;

        /// <summary>可信度：MSFT_NetAdapter + 设备管理器属性。参数：无。</summary>
        public const byte ConfidenceNetAdapter = 25;

        /// <summary>可信度：Windows PnP 服务角色 + 设备管理器属性。参数：无。</summary>
        public const byte ConfidencePnpServiceRole = 26;

        /// <summary>可信度：USB Hub IOCTL 连接器属性。参数：无。</summary>
        public const byte ConfidenceUsbConnectorProperties = 27;

        /// <summary>可信度：Windows 活动显示路径。参数：无。</summary>
        public const byte ConfidenceActiveDisplayPath = 28;

        /// <summary>可信度：机型接口档案 / Windows 显示目标。参数：无。</summary>
        public const byte ConfidenceOemProfileDisplayTarget = 29;

        /// <summary>来源：USB Hub IOCTL + WMI USB 关系链 + SetupAPI / CfgMgr32 + PnP。参数：无。</summary>
        public const byte SourceUsbIoctlWmiSetupApiPnp = 30;

        /// <summary>来源：USB Hub IOCTL + SetupAPI / CfgMgr32 + PnP。参数：无。</summary>
        public const byte SourceUsbIoctlSetupApiPnp = 31;

        /// <summary>来源：WMI USB 关系链 + SetupAPI / CfgMgr32 + PnP。参数：无。</summary>
        public const byte SourceWmiSetupApiPnp = 32;

        /// <summary>来源：WMI USB 关系链 + PnP。参数：无。</summary>
        public const byte SourceWmiPnp = 33;

        /// <summary>来源：SetupAPI / CfgMgr32 + PnP。参数：无。</summary>
        public const byte SourceSetupApiPnp = 34;

        /// <summary>来源：PnP 设备枚举。参数：无。</summary>
        public const byte SourcePnpEnumeration = 35;

        /// <summary>来源：MSFT_NetAdapter + SetupAPI / CfgMgr32 + PnP。参数：无。</summary>
        public const byte SourceNetAdapterSetupApiPnp = 36;

        /// <summary>来源：Windows PnP 服务 + SetupAPI / CfgMgr32。参数：无。</summary>
        public const byte SourcePnpServiceSetupApi = 37;

        /// <summary>来源：USB Hub IOCTL。参数：无。</summary>
        public const byte SourceUsbHubIoctl = 38;

        /// <summary>来源：QueryDisplayConfig + DisplayConfigGetDeviceInfo。参数：无。</summary>
        public const byte SourceQueryDisplayConfig = 39;

        /// <summary>来源：OEM 机型接口档案 + QueryDisplayConfig(QDC_ALL_PATHS)。参数：无。</summary>
        public const byte SourceOemProfileQueryDisplayConfig = 40;

        /// <summary>高级互联的判定依据是某个 Windows PnP 服务。参数：1 = 服务名。</summary>
        public const byte InterconnectEvidencePnpService = 41;

        /// <summary>互联角色：USB Type-C 连接器管理器。参数：无。</summary>
        public const byte RoleUcsiConnectorManager = 42;

        /// <summary>互联角色：USB4 主机路由器。参数：无。</summary>
        public const byte RoleUsb4HostRouter = 43;

        /// <summary>互联角色：USB4 / Thunderbolt 3 设备路由器。参数：无。</summary>
        public const byte RoleUsb4DeviceRouter = 44;

        /// <summary>互联角色：USB4 主机互联网络适配器。参数：无。</summary>
        public const byte RoleUsb4P2PNetwork = 45;

        /// <summary>USB 端口连接状态：没有设备。参数：无。</summary>
        public const byte UsbNotConnected = 46;

        /// <summary>USB 端口连接状态：已连接。参数：无。</summary>
        public const byte UsbConnected = 47;

        /// <summary>USB 端口连接状态：枚举失败。参数：无。</summary>
        public const byte UsbEnumerationFailed = 48;

        /// <summary>USB 端口连接状态：设备通用故障。参数：无。</summary>
        public const byte UsbDeviceGeneralFailure = 49;

        /// <summary>USB 端口连接状态：设备引发过流。参数：无。</summary>
        public const byte UsbDeviceCausedOvercurrent = 50;

        /// <summary>USB 端口连接状态：电源不足。参数：无。</summary>
        public const byte UsbInsufficientPower = 51;

        /// <summary>USB 端口连接状态：带宽不足。参数：无。</summary>
        public const byte UsbInsufficientBandwidth = 52;

        /// <summary>USB 端口连接状态：Hub 嵌套过深。参数：无。</summary>
        public const byte UsbHubNestedTooDeep = 53;

        /// <summary>USB 端口连接状态：设备位于旧版 Hub。参数：无。</summary>
        public const byte UsbDeviceInLegacyHub = 54;

        /// <summary>USB 端口连接状态：枚举中。参数：无。</summary>
        public const byte UsbEnumerating = 55;

        /// <summary>USB 端口连接状态：重置中。参数：无。</summary>
        public const byte UsbResetting = 56;

        /// <summary>USB 端口连接状态：Windows 返回了未定义的状态值。参数：1 = 原始状态号。</summary>
        public const byte UsbUnknownStatus = 57;

        /// <summary>能力来源：Win32_DiskDrive / DiskPartition / LogicalDisk 关联。参数：无。</summary>
        public const byte SourceWin32DiskAssociations = 58;

        /// <summary>能力来源：MSFT_Disk 加 Win32 的磁盘/分区/卷关联。参数：无。</summary>
        public const byte SourceMsftDiskAssociations = 59;

        /// <summary>能力来源：USB HID 描述符与端点描述符。参数：无。</summary>
        public const byte SourceUsbHidEndpointDescriptors = 60;

        /// <summary>能力来源：USB Video Class 配置描述符。参数：无。</summary>
        public const byte SourceUsbVideoClassDescriptors = 61;

        /// <summary>能力来源：USB 设备/配置描述符加 Windows WPD / PnP 属性。参数：无。</summary>
        public const byte SourceUsbDescriptorsAndWindows = 62;

        /// <summary>能力来源：Windows WPD / PnP 属性。参数：无。</summary>
        public const byte SourceWindowsWpdPnp = 63;
    }
}

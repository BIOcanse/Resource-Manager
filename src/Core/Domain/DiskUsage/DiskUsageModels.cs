namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>卷的介质来源。用户要能一眼看出这是真盘还是虚拟盘。</summary>
public static class DiskUsageVolumeKinds
{
    /// <summary>本机物理磁盘上的分区。</summary>
    public const string Physical = "physical";

    /// <summary>虚拟磁盘：挂载的 VHD/VHDX、存储空间、iSCSI，或 subst 出来的盘符。</summary>
    public const string Virtual = "virtual";

    /// <summary>可移动介质：U 盘、读卡器。</summary>
    public const string Removable = "removable";

    /// <summary>网络映射盘。</summary>
    public const string Network = "network";

    /// <summary>光驱。</summary>
    public const string Optical = "optical";

    /// <summary>判断不出来时用它，不猜。</summary>
    public const string Unknown = "unknown";
}

/// <summary>这次扫描实际走了哪条路。不允许静默降级，所以它必须跟着结果一起给出来。</summary>
public static class DiskUsageScanKinds
{
    /// <summary>直接读 NTFS 的主文件表，整盘一次读完。需要管理员权限。</summary>
    public const string MasterFileTable = "masterFileTable";

    /// <summary>逐级遍历目录。哪儿都能用，但慢。</summary>
    public const string DirectoryWalk = "directoryWalk";
}

/// <summary>扫描覆盖到哪里。</summary>
public static class DiskUsageScanScopes
{
    /// <summary>所有已就绪的本地卷。</summary>
    public const string AllVolumes = "allVolumes";

    /// <summary>一个卷。</summary>
    public const string Volume = "volume";

    /// <summary>一个绝对路径下的子树。路径由系统的文件夹对话框选出来。</summary>
    public const string Folder = "folder";
}

/// <summary>
/// 用哪种方式扫。用户显式选，程序不替他降级。
/// </summary>
public static class DiskUsageScanModes
{
    /// <summary>
    /// 快速扫描：读文件系统索引。碰到不支持索引的卷就跳过并说明原因，
    /// 不偷偷改用遍历 —— 覆盖那些卷是「全扫描」的职责。
    /// </summary>
    public const string Fast = "fast";

    /// <summary>全扫描：逐级遍历目录。慢，但哪儿都能扫。</summary>
    public const string Full = "full";
}

/// <summary>一次扫描请求。</summary>
public sealed record DiskUsageScanRequest(
    /// <summary>见 <see cref="DiskUsageScanScopes"/>。</summary>
    string Scope,
    /// <summary>见 <see cref="DiskUsageScanModes"/>。</summary>
    string Mode,
    /// <summary>
    /// 范围是卷时填盘符（<c>C:</c>），是文件夹时填绝对路径，
    /// 范围是全局时为空串。
    /// </summary>
    string Target);

/// <summary>某个目标没能扫成，原因是什么。</summary>
public static class DiskUsageSkipReasons
{
    /// <summary>快速扫描要求文件系统索引，这个卷的文件系统没有。</summary>
    public const string NoFileSystemIndex = "noFileSystemIndex";

    /// <summary>读索引需要管理员权限，当前没有。</summary>
    public const string NeedsElevation = "needsElevation";

    /// <summary>卷没就绪（空光驱、拔掉的 U 盘）。</summary>
    public const string VolumeNotReady = "volumeNotReady";

    /// <summary>路径不存在或打不开。</summary>
    public const string TargetUnavailable = "targetUnavailable";
}

/// <summary>一个被跳过的目标，连同原因。扫描结果里必须列出来，不能悄悄少扫。</summary>
public sealed record DiskUsageSkippedTarget(
    string Target,
    /// <summary>见 <see cref="DiskUsageSkipReasons"/>。</summary>
    string Reason);

/// <summary>一个可以扫描的卷。</summary>
public sealed record DiskUsageVolume(
    /// <summary>盘符，形如 <c>C:</c>。同时是这个卷的稳定标识。</summary>
    string VolumeId,
    /// <summary>卷标；没有就是空串，由前端出兜底名。</summary>
    string Label,
    /// <summary>文件系统名，例如 NTFS、exFAT；读不到是空串。</summary>
    string FileSystem,
    /// <summary>介质来源，见 <see cref="DiskUsageVolumeKinds"/>。</summary>
    string VolumeKind,
    ulong TotalBytes,
    ulong FreeBytes,
    /// <summary>卷已就绪、可以扫。没就绪的卷仍然列出来，但不能扫。</summary>
    bool IsReady,
    /// <summary>这个卷能不能走主文件表快路径（NTFS + 有权限）。</summary>
    bool SupportsMasterFileTable);

/// <summary>一次扫描完成后的概况。树本身留在后端，这里只给用户看的事实。</summary>
public sealed record DiskUsageScanSummary(
    string Scope,
    string Mode,
    /// <summary>请求里的目标，原样回显。</summary>
    string Target,
    /// <summary>实际扫到的每个根（全局扫描会有多个）。</summary>
    IReadOnlyList<string> Roots,
    /// <summary>实际用的扫描方式，见 <see cref="DiskUsageScanKinds"/>。</summary>
    string ScanKind,
    /// <summary>没扫的目标和原因。为空表示这次扫描覆盖了请求的全部范围。</summary>
    IReadOnlyList<DiskUsageSkippedTarget> Skipped,
    DateTimeOffset CompletedAt,
    double DurationSeconds,
    ulong TotalBytes,
    ulong ScannedBytes,
    long FileCount,
    long DirectoryCount,
    /// <summary>因为权限或占用读不到的条目数。为 0 表示这次扫描是完整的。</summary>
    long SkippedCount);

/// <summary>树里的一个节点，供前端做提示和右键菜单。</summary>
public sealed record DiskUsageNode(
    /// <summary>节点在当前扫描里的稳定序号。换一次扫描就重新编号。</summary>
    uint NodeId,
    uint ParentNodeId,
    string Name,
    string FullPath,
    /// <summary>目录节点为 true。</summary>
    bool IsDirectory,
    /// <summary>这个节点自己的字节数；目录是整棵子树的和。</summary>
    ulong SizeBytes,
    /// <summary>占用的簇字节数；读不到时与 <see cref="SizeBytes"/> 相同。</summary>
    ulong AllocatedBytes,
    long FileCount,
    DateTimeOffset? LastWriteAt);

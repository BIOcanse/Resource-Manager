using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Domain.ProcessAttribution;

public static class RuntimeAttributionIds
{
    public const string ResourceManagerSelf = "resource-manager:self";
    public const string WindowsSystem = "windows:system";
    public const string WindowsComponent = "windows:component";
    public const string Unattributed = "unattributed";
}

public sealed record RuntimeSoftwareAttribution(
    string Id,
    string Name,
    string Kind,
    string DisplayKind,
    IReadOnlyList<string> RootPaths)
{
    public static RuntimeSoftwareAttribution Unattributed { get; } = new(
        RuntimeAttributionIds.Unattributed,
        // 名字为空表示「没有可显示的软件名」，前端按 Kind 出「未归属进程」。
        string.Empty,
        SoftwareKinds.Unattributed,
        SoftwareDisplayKinds.Unattributed,
        []);
}

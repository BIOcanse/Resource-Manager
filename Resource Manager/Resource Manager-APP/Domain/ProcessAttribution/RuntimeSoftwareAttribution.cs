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
        "未归属进程",
        SoftwareKinds.Unattributed,
        "未归属进程",
        []);
}

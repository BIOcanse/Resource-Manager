namespace ResourceManager.Shared.Runtime.TrustedAccess;

public static class AccessKeyScopes
{
    public const string Read = "read";
    public const string MetricsRead = "metrics.read";
    public const string ProcessesRead = "processes.read";
    public const string Session = "session";
    public const string AdaptersRegisterSelf = "adapters.register.self";
    public const string DesktopPublish = "desktop.publish";
    public const string GpuResolve = "gpu.resolve";
    public const string GpuReport = "gpu.report";
    public const string KeysManage = "keys.manage";
    public const string MachineWrite = "machine.write";
    public const string InjectionConfigure = "injection.configure";

    private static readonly string[] defaultScopes =
    [
        Read,
        MetricsRead,
        ProcessesRead,
        Session,
        AdaptersRegisterSelf,
        DesktopPublish,
        GpuResolve,
        GpuReport,
        KeysManage
    ];

    private static readonly HashSet<string> allScopes = new(
        [.. defaultScopes, MachineWrite, InjectionConfigure],
        StringComparer.Ordinal);

    public static string[] Default => [.. defaultScopes];

    public static bool IsSupported(string? value) =>
        value is not null && allScopes.Contains(value);
}

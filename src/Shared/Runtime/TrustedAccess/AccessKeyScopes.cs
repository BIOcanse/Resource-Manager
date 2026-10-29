namespace ResourceManager.Shared.Runtime.TrustedAccess;

/// <summary>
/// One row of the scope table: whether the default key carries the scope, whether a keyless local Windows
/// caller has it, and whether a process presenting a key that carries it must be an elevated administrator.
/// </summary>
public sealed record AccessKeyScopeDefinition(
    string Name,
    bool InDefaultKey,
    bool KeylessLocal,
    bool RequiresAdministratorHolder);

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

    // The scope table (docs/trusted_access_scopes_2026-10-29.md, user 10-29). The default key, the keyless local
    // scopes and the scopes whose holder must be an elevated administrator are all read from it; the bundled UI
    // runs unelevated, so no default-key scope may require an administrator holder.
    public static IReadOnlyList<AccessKeyScopeDefinition> Definitions { get; } =
    [
        new(Read, InDefaultKey: true, KeylessLocal: true, RequiresAdministratorHolder: false),
        new(MetricsRead, InDefaultKey: true, KeylessLocal: true, RequiresAdministratorHolder: false),
        new(ProcessesRead, InDefaultKey: true, KeylessLocal: true, RequiresAdministratorHolder: false),
        new(Session, InDefaultKey: true, KeylessLocal: false, RequiresAdministratorHolder: false),
        new(AdaptersRegisterSelf, InDefaultKey: true, KeylessLocal: false, RequiresAdministratorHolder: false),
        new(DesktopPublish, InDefaultKey: true, KeylessLocal: false, RequiresAdministratorHolder: false),
        new(GpuResolve, InDefaultKey: true, KeylessLocal: false, RequiresAdministratorHolder: false),
        new(GpuReport, InDefaultKey: true, KeylessLocal: false, RequiresAdministratorHolder: false),
        new(KeysManage, InDefaultKey: true, KeylessLocal: false, RequiresAdministratorHolder: false),
        new(MachineWrite, InDefaultKey: false, KeylessLocal: false, RequiresAdministratorHolder: true),
        new(InjectionConfigure, InDefaultKey: false, KeylessLocal: false, RequiresAdministratorHolder: true)
    ];

    private static readonly string[] defaultScopes = Names(static scope => scope.InDefaultKey);

    private static readonly HashSet<string> allScopes = new(Names(static _ => true), StringComparer.Ordinal);

    private static readonly HashSet<string> administratorHolderScopes =
        new(Names(static scope => scope.RequiresAdministratorHolder), StringComparer.Ordinal);

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Names(static _ => true));

    public static IReadOnlyList<string> KeylessLocal { get; } = Array.AsReadOnly(Names(static scope => scope.KeylessLocal));

    public static string[] Default => [.. defaultScopes];

    public static bool IsSupported(string? value) =>
        value is not null && allScopes.Contains(value);

    /// <summary>True when the scopes include one whose holder must be an elevated administrator.</summary>
    public static bool RequiresAdministratorHolder(IReadOnlyList<string> scopes)
    {
        for (var index = 0; index < scopes.Count; index++)
            if (administratorHolderScopes.Contains(scopes[index])) return true;
        return false;
    }

    private static string[] Names(Func<AccessKeyScopeDefinition, bool> selected)
        => Definitions.Where(selected).Select(static scope => scope.Name).ToArray();
}

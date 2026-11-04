using System.Globalization;
using System.Security;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ResourceManager.ReadWrite.Windows.Platform;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Explicit, stateless selected provider reads. Construction is passive.
/// Run/RunOnce are registry configuration declarations, not launch validation.</summary>
public sealed class WindowsPlatformDeclarationReader
{
    public static readonly Guid WindowsLicensingApplicationId = new("55c92734-d682-4d71-983e-d6ec3f16059f");
    private readonly WindowsPlatformDeclarationScope scope;
    private readonly TimeSpan timeout;
    private readonly Func<string, string, TimeSpan, QueryResult> query;
    private readonly Func<WindowsOperatingSystemInformationFields, CancellationToken, WindowsPlatformDeclarationFacts>? recorded;

    public WindowsPlatformDeclarationReader(WindowsPlatformDeclarationScope? scope = null, TimeSpan? queryTimeout = null)
    {
        this.scope = scope ?? new(); timeout = queryTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(queryTimeout));
        if (this.scope.UserSecurityId is { } sid && !ValidSid(sid)) throw new ArgumentException("An explicit user SID is required.", nameof(scope));
        query = PlatformSensorWmiUtilities.QueryObjects;
    }
    internal WindowsPlatformDeclarationReader(Func<WindowsOperatingSystemInformationFields, CancellationToken, WindowsPlatformDeclarationFacts> recorded)
        : this() { this.recorded = recorded; }

    public WindowsPlatformDeclarationFacts Read(WindowsOperatingSystemInformationFields selected, CancellationToken token = default,
        Action<WindowsOperatingSystemInformationFields>? sourceQueryAttempted = null)
    {
        if ((selected & ~WindowsOperatingSystemInformationFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(selected));
        token.ThrowIfCancellationRequested();
        const WindowsOperatingSystemInformationFields platform = WindowsOperatingSystemInformationFields.Activation
            | WindowsOperatingSystemInformationFields.Environment | WindowsOperatingSystemInformationFields.Startup | WindowsOperatingSystemInformationFields.Packages
            | WindowsOperatingSystemInformationFields.SessionId;
        if ((selected & platform) == 0) return new(null, null, null, null);
        if (recorded is not null)
        {
            var recordedSelection = selected & platform;
            if (recordedSelection != WindowsOperatingSystemInformationFields.None)
                sourceQueryAttempted?.Invoke(recordedSelection);
            return recorded(selected, token);
        }
        if (!OperatingSystem.IsWindows()) return new(null, null, null, null);
        return ReadWindows(selected, token, sourceQueryAttempted);
    }

    [SupportedOSPlatform("windows")]
    private WindowsPlatformDeclarationFacts ReadWindows(WindowsOperatingSystemInformationFields selected, CancellationToken token,
        Action<WindowsOperatingSystemInformationFields>? sourceQueryAttempted)
    {
        string? sid = scope.UserSecurityId;
        if (scope.IncludeUserConfiguration && sid is null && (selected & (WindowsOperatingSystemInformationFields.Environment
                | WindowsOperatingSystemInformationFields.Startup | WindowsOperatingSystemInformationFields.Packages)) != 0)
            sid = Attempt(() => { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value; });
        IReadOnlyList<WindowsLicenseStatusRow>? activation = null;
        if ((selected & WindowsOperatingSystemInformationFields.Activation) != 0)
        {
            sourceQueryAttempted?.Invoke(WindowsOperatingSystemInformationFields.Activation);
            activation = Attempt(() => ReadActivation(token));
        }
        var environment = (selected & WindowsOperatingSystemInformationFields.Environment) != 0
            ? Attempt(() => ReadRegistry(sid, false, token, sourceQueryAttempted)) : null;
        var startup = (selected & WindowsOperatingSystemInformationFields.Startup) != 0
            ? Attempt(() => ReadRegistry(sid, true, token, sourceQueryAttempted)) : null;
        var startupAdditional = startup is not null
            ? Attempt(() => new WindowsStartupConfigurationReader().Read(scope, sid, token, sourceQueryAttempted)) : null;
        if ((selected & WindowsOperatingSystemInformationFields.Startup) != 0 && startupAdditional is null) startup = null;
        IReadOnlyList<WindowsRegisteredPackageFamily>? packages = null;
        if ((selected & WindowsOperatingSystemInformationFields.Packages) != 0
            && scope.IncludeUserConfiguration && sid is not null)
        {
            sourceQueryAttempted?.Invoke(WindowsOperatingSystemInformationFields.Packages);
            packages = Attempt(() => WindowsRegisteredPackageReader.Read(sid, token));
        }
        IReadOnlyList<WindowsLoginSession>? sessions = null;
        if ((selected & WindowsOperatingSystemInformationFields.SessionId) != 0)
        {
            sourceQueryAttempted?.Invoke(WindowsOperatingSystemInformationFields.SessionId);
            sessions = new WindowsSessionInformationReader().Read(token);
        }
        token.ThrowIfCancellationRequested();
        return new(activation, environment, startup, packages)
        {
            StartupAdditional = startup is null ? null : startupAdditional,
            Sessions = sessions
        };
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "Windows SoftwareLicensingProduct license status changed")]
    private IReadOnlyList<WindowsLicenseStatusRow>? ReadActivation(CancellationToken token)
    {
        var result = query(@"root\CIMV2", "SELECT ID, ApplicationID, LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f'", timeout);
        if (result.Error is not null) return null;
        var rows = new List<WindowsLicenseStatusRow>();
        var identities = new HashSet<Guid>();
        foreach (var row in result.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (!row.TryGetValue("ApplicationID", out var app) || !Guid.TryParse(app as string, out var application)) return null;
            if (application != WindowsLicensingApplicationId) continue;
            if (!row.TryGetValue("ID", out var id) || !Guid.TryParse(id as string, out var product) || !identities.Add(product)) return null;
            uint? status = row.TryGetValue("LicenseStatus", out var value) ? DecodeStatus(value) : null;
            rows.Add(new(application, product, status));
        }
        return rows.ToArray();
    }
    internal static uint? DecodeStatus(object? value) => value switch
    {
        uint number => number, int number when number >= 0 => (uint)number,
        string text when uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
        _ => null
    };

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<WindowsConfiguredRegistryString>? ReadRegistry(string? sid, bool startup, CancellationToken token,
        Action<WindowsOperatingSystemInformationFields>? sourceQueryAttempted)
    {
        if (scope.IncludeUserConfiguration && sid is null) return null;
        var rows = new List<WindowsConfiguredRegistryString>();
        var views = startup && Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32 };
        var category = startup ? WindowsOperatingSystemInformationFields.Startup : WindowsOperatingSystemInformationFields.Environment;
        foreach (var view in views)
        {
            token.ThrowIfCancellationRequested();
            if (scope.IncludeMachineConfiguration)
            {
                sourceQueryAttempted?.Invoke(category);
                ReadHive(RegistryHive.LocalMachine, "HKLM", view, null, rows, startup, token);
            }
            if (scope.IncludeUserConfiguration)
            {
                sourceQueryAttempted?.Invoke(category);
                ReadHive(RegistryHive.Users, "HKU", view, sid, rows, startup, token);
            }
        }
        return rows.ToArray();
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Machine or explicit-user environment registry key changed")]
    private static void ReadHive(RegistryHive hive, string hiveName, RegistryView view, string? sid,
        List<WindowsConfiguredRegistryString> rows, bool startup, CancellationToken token)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        // A missing requested user hive is not proof of an empty user's configuration.
        if (sid is not null) { using var loaded = root.OpenSubKey(sid, false); if (loaded is null) throw new IOException("Requested user hive is not loaded."); }
        var prefix = sid is null ? "" : sid + @"\";
        var paths = startup ? new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" }
            : new[] { sid is null ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : "Environment" };
        foreach (var relative in paths)
        {
            token.ThrowIfCancellationRequested();
            var path = prefix + relative;
            using var key = root.OpenSubKey(path, false);
            if (key is null) continue; // Existing loaded scope, missing optional branch: empty.
            foreach (var name in key.GetValueNames())
            {
                token.ThrowIfCancellationRequested();
                var kind = key.GetValueKind(name);
                var text = kind is RegistryValueKind.String or RegistryValueKind.ExpandString
                    ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string : null;
                rows.Add(new(hiveName, view, sid, relative, name, kind, text));
            }
        }
    }
    private static bool ValidSid(string sid)
    {
        // Syntax validation is passive and portable; actual account access is the OS read's decision.
        var parts = sid.Split('-');
        return parts.Length >= 4 && parts[0] == "S" && parts[1] == "1"
            && ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && parts.Skip(3).All(static part => uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
    private static T? Attempt<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or PlatformNotSupportedException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        { return null; }
    }
}

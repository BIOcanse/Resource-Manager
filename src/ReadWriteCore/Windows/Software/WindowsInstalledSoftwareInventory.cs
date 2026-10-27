using Microsoft.Win32;
using ResourceManager.ReadWrite;
using ResourceManager.App.Infrastructure.Monitoring;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.App.Infrastructure.Software;

public sealed partial class WindowsInstalledSoftwareInventory : IInstalledSoftwareInventory
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string DefaultSoftwareScope = "<default>";
    private static readonly string? WindowsRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private sealed record InventoryCapture(string? UserSid, IReadOnlyList<InstalledSoftwareEntry> Entries, DeviceInformationSnapshot Fields);
    private InventoryCapture? current;
    // Keep service information separate from the interactive caller. Alternating
    // attribution and user reads must not invalidate each other's inventory.
    private InventoryCapture? callerCurrent;
    private InventoryCapture? ReadCurrent(string? userSid)
        => Volatile.Read(ref (userSid is null ? ref current : ref callerCurrent));
    private readonly Dictionary<string, string> committedSoftwareFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly CurrentValuePublicationSignal publications = new();
    private readonly Func<CancellationToken, IReadOnlyList<InstalledSoftwareEntry>> read;
    private readonly bool hasInjectedReader;
    private readonly TimeProvider clock;
    private readonly Action? onSoftwareContentChanged;
    private readonly InformationReadWriteBackend backend;
    private readonly SamplingPublicationSignal collectionPublications = new();

    /// <summary>Reads machine-wide entries and the explicitly selected user's uninstall entries.
    /// Construction performs no scan. The optional callback runs after an accepted content change.</summary>
    public WindowsInstalledSoftwareInventory(Func<CancellationToken, IReadOnlyList<InstalledSoftwareEntry>>? read = null,
        TimeProvider? clock = null,
        Action? onSoftwareContentChanged = null)
    {
        this.read = read ?? (token => ScanInstalledSoftware(token, null));
        hasInjectedReader = read is not null;
        this.clock = clock ?? TimeProvider.System;
        this.onSoftwareContentChanged = onSoftwareContentChanged;
        backend = new(ReadInformation, token => CaptureInformationAsync(token), static _ => new(true, null, []));
    }

    public InformationReadWriteBackend CreateBackend() => backend;

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
        => new InformationCollectionBorrow(backend, collectionPublications, "information.software",
            async (selection, permit, token) =>
            {
                var committed = false;
                var snapshot = await ReadOrCaptureInformationAsync(token, commit =>
                {
                    committed = permit.TryPublish(commit);
                    return committed;
                });
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, true));
            }, FieldCollectionSourceKind.SourceQuery);
    /// <summary>EventDriven subscription acquisition: initialize once, then consume
    /// the current inventory until explicit refresh commits a replacement.</summary>
    [RuntimeReadCache]
    public async ValueTask<DeviceInformationSnapshot> ReadOrCaptureInformationAsync(CancellationToken cancellationToken,
        Func<Action, bool>? tryPublish = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref current) is not null)
        {
            tryPublish?.Invoke(static () => { });
            return ReadInformation();
        }
        await LoadAsync(false, userSid: null, cancellationToken, tryPublish, clearFieldsOnFailure: true);
        return ReadInformation();
    }

    public DeviceInformationSnapshot ReadInformation()
    {
        var snapshot = Volatile.Read(ref current);
        return snapshot is { UserSid: null }
            ? snapshot.Fields
            : DeviceInformationSnapshot.Empty;
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "Explicit installed software refresh requested")]
    public async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken,
        Func<Action, bool>? tryPublish = null)
    {
        await LoadAsync(true, userSid: null, cancellationToken, tryPublish, clearFieldsOnFailure: true);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadInformation();
    }

    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var subscription = publications.Subscribe();
        yield return ReadInformation();
        while (!cancellationToken.IsCancellationRequested)
        {
            await subscription.WaitAsync(cancellationToken);
            yield return ReadInformation();
        }
    }

    public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
        CancellationToken cancellationToken)
    {
        return LoadAsync(forceRefresh: false, userSid: null, cancellationToken);
    }

    public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
        string? userSid,
        CancellationToken cancellationToken)
    {
        return LoadAsync(forceRefresh: false, NormalizeUserSid(userSid), cancellationToken);
    }

    public Task<IReadOnlyList<InstalledSoftwareEntry>> RefreshInstalledSoftwareAsync(
        CancellationToken cancellationToken)
    {
        return LoadAsync(forceRefresh: true, userSid: null, cancellationToken);
    }

    public Task<IReadOnlyList<InstalledSoftwareEntry>> RefreshInstalledSoftwareAsync(
        string? userSid,
        CancellationToken cancellationToken)
    {
        return LoadAsync(forceRefresh: true, NormalizeUserSid(userSid), cancellationToken);
    }

    private async Task<IReadOnlyList<InstalledSoftwareEntry>> LoadAsync(
        bool forceRefresh,
        string? userSid,
        CancellationToken cancellationToken,
        Func<Action, bool>? tryPublish = null,
        bool clearFieldsOnFailure = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = ReadCurrent(userSid);
        if (!forceRefresh && previous is not null && previous.UserSid == userSid)
        {
            return previous.Entries;
        }

        await refreshGate.WaitAsync(cancellationToken);
        var contentChanged = false;
        IReadOnlyList<InstalledSoftwareEntry> result;
        try
        {
            previous = ReadCurrent(userSid);
            if (!forceRefresh && previous is not null && previous.UserSid == userSid)
            {
                result = previous.Entries;
            }
            else
            {
                InventoryCapture next;
                var capturedSoftwareContent = true;
                try
                {
                    var refreshed = hasInjectedReader
                        ? read(cancellationToken)
                        : ScanInstalledSoftware(cancellationToken, userSid);
                    cancellationToken.ThrowIfCancellationRequested();
                    next = new(userSid, refreshed, SoftwareInformationFieldProjection.Create(refreshed, clock.GetUtcNow()));
                }
                catch (Exception error) when (clearFieldsOnFailure && (error is IOException or UnauthorizedAccessException))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sameScope = previous?.UserSid == userSid;
                    next = new(userSid, sameScope ? previous!.Entries : [],
                        SoftwareInformationFieldProjection.Clear(sameScope ? previous!.Fields : DeviceInformationSnapshot.Empty));
                    capturedSoftwareContent = false;
                }

                var fingerprint = capturedSoftwareContent ? FingerprintSoftwareContent(next.Entries) : null;
                void Commit()
                {
                    if (userSid is null) Volatile.Write(ref current, next);
                    else Volatile.Write(ref callerCurrent, next);
                    if (fingerprint is not null)
                    {
                        var scope = userSid ?? DefaultSoftwareScope;
                        if (committedSoftwareFingerprints.TryGetValue(scope, out var previousFingerprint))
                        {
                            if (!string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                            {
                                committedSoftwareFingerprints[scope] = fingerprint;
                                contentChanged = true;
                            }
                        }
                        else
                        {
                            committedSoftwareFingerprints.Add(scope, fingerprint);
                        }
                    }
                    publications.Publish();
                    collectionPublications.Publish(["information.software"]);
                }

                if (tryPublish is null)
                {
                    Commit();
                    result = next.Entries;
                }
                else if (!tryPublish(Commit))
                {
                    var visible = ReadCurrent(userSid);
                    result = visible is not null && visible.UserSid == userSid ? visible.Entries : [];
                }
                else
                {
                    result = next.Entries;
                }
            }
        }
        finally
        {
            refreshGate.Release();
        }

        if (contentChanged)
        {
            onSoftwareContentChanged?.Invoke();
        }
        return result;
    }

    private static string FingerprintSoftwareContent(IReadOnlyList<InstalledSoftwareEntry> entries)
    {
        var content = entries
            .OrderBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.RegistryPath, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => new SoftwareContentDigestInput(entry.Id, entry.Name, entry.Version, entry.Publisher, entry.InstallLocation,
                entry.RootPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(), entry.UninstallString, entry.RegistryPath, entry.ReportedSizeBytes))
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, SoftwareContentJsonContext.Default.SoftwareContentDigestInputArray)));
    }

    private static IReadOnlyList<InstalledSoftwareEntry> ScanInstalledSoftware(
        CancellationToken cancellationToken,
        string? userSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows uninstall inventory requires Windows.");
        var records = new Dictionary<string, InstalledSoftwareEntry>(StringComparer.OrdinalIgnoreCase);

        var includeCurrentUser = IncludesCurrentUser(userSid, WindowsIdentity.GetCurrent);
        var selectedUserSids = userSid is null ? Array.Empty<string>() : [userSid];
        foreach (var root in CreateInventoryRoots(selectedUserSids, includeCurrentUser))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadUninstallRoot(root, records, cancellationToken);
        }

        return NormalizeSharedInstallRoots(ReadSteamLibraries(records.Values.ToArray(), cancellationToken))
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Publisher, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    internal static bool IncludesCurrentUser(string? userSid, Func<WindowsIdentity> openIdentity)
    {
        if (userSid is not null) return false;
        using var identity = openIdentity();
        return !identity.IsSystem;
    }

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<RegistryInventoryRoot> CreateInventoryRoots(
        IEnumerable<string> activeUserSids,
        bool includeCurrentUser = false)
    {
        var roots = new List<RegistryInventoryRoot>
        {
            new(RegistryHive.LocalMachine, RegistryView.Registry64, null),
            new(RegistryHive.LocalMachine, RegistryView.Registry32, null)
        };

        if (includeCurrentUser)
        {
            roots.Add(new(RegistryHive.CurrentUser, RegistryView.Registry64, null));
            roots.Add(new(RegistryHive.CurrentUser, RegistryView.Registry32, null));
        }

        var seenSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in activeUserSids)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            string sid;
            try
            {
                sid = new SecurityIdentifier(value).Value;
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (sid.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase) || !seenSids.Add(sid)) continue;
            roots.Add(new(RegistryHive.Users, RegistryView.Registry64, sid));
            roots.Add(new(RegistryHive.Users, RegistryView.Registry32, sid));
        }

        return roots;
    }

    private static string? NormalizeUserSid(string? userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid)) return null;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("User SID selection requires Windows.");
        return new SecurityIdentifier(userSid).Value;
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Explicit installed software refresh requested")]
    private static void ReadUninstallRoot(
        RegistryInventoryRoot root,
        Dictionary<string, InstalledSoftwareEntry> records,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(root.Hive, root.View);
            var uninstallPath = root.UserSid is null
                ? UninstallSubKey
                : $"{root.UserSid}\\{UninstallSubKey}";
            using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadInventoryEntry(subKeyName, name =>
                {
                    using var appKey = uninstallKey.OpenSubKey(name);
                    if (appKey is null)
                    {
                        return;
                    }

                    var entry = ReadEntry(root, name, appKey);
                    if (entry is null)
                    {
                        return;
                    }

                    var key = BuildDeduplicationKey(entry);
                    if (!records.TryGetValue(key, out var existing) || PreferEntry(entry, existing))
                    {
                        records[key] = entry;
                    }
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Cannot enumerate uninstall inventory root {root.Hive}/{root.View}/{root.UserSid}: {ex.Message}");
        }
    }

    internal static void ReadInventoryEntry(string name, Action<string> readEntry)
    {
        try { readEntry(name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Cannot read uninstall inventory entry {name}: {error.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Explicit installed software refresh requested")]
    private static InstalledSoftwareEntry? ReadEntry(
        RegistryInventoryRoot root,
        string subKeyName,
        RegistryKey appKey)
    {
        var isUserScoped = root.UserSid is not null;
        var name = ReadString(appKey, "DisplayName", isUserScoped);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (ReadInt(appKey, "SystemComponent", isUserScoped) == 1)
        {
            return null;
        }

        var releaseType = ReadString(appKey, "ReleaseType", isUserScoped);
        if (releaseType is not null
            && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var hiveName = root.UserSid is null ? root.Hive.ToString() : $"{root.Hive}\\{root.UserSid}";
        var registryPath = $@"{hiveName}\{root.View}\{UninstallSubKey}\{subKeyName}";
        var publisher = ReadString(appKey, "Publisher", isUserScoped);
        var version = ReadString(appKey, "DisplayVersion", isUserScoped);
        var installLocation = NormalizeInstallLocation(ReadString(appKey, "InstallLocation", isUserScoped), isUserScoped);
        var displayIcon = ReadString(appKey, "DisplayIcon", isUserScoped);
        var uninstallString = ReadString(appKey, "QuietUninstallString", isUserScoped)
            ?? ReadString(appKey, "UninstallString", isUserScoped);
        var rootPaths = BuildRootPaths(installLocation, uninstallString, displayIcon, expandEnvironment: !isUserScoped);
        if (!HasValidRegisteredLocation(rootPaths))
        {
            return null;
        }

        return new InstalledSoftwareEntry(
            $"windows-installed:{NormalizeId(registryPath)}",
            name.Trim(),
            string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            installLocation,
            rootPaths,
            string.IsNullOrWhiteSpace(uninstallString) ? null : uninstallString.Trim(),
            registryPath,
            ReadEstimatedSizeBytes(appKey));
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Explicit installed software refresh requested")]
    internal static ulong? ReadEstimatedSizeBytes(RegistryKey key)
        => ReadEstimatedSizeBytes(() => key.GetValue("EstimatedSize"), () => key.GetValueKind("EstimatedSize"));

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Explicit installed software refresh requested")]
    internal static ulong? ReadEstimatedSizeBytes(Func<object?> readValue, Func<RegistryValueKind> readKind)
    {
        try
        {
            var value = readValue();
            if (value is not int) return null;
            return DecodeEstimatedSizeBytes(value, readKind());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    // REG_DWORD is unsigned; RegistryKey exposes its bits as Int32. Other kinds
    // and numeric strings are not the formal Windows Installer EstimatedSize value.
    internal static ulong? DecodeEstimatedSizeBytes(object? value, RegistryValueKind kind)
        => kind == RegistryValueKind.DWord && value is int bits
            ? (ulong)unchecked((uint)bits) * 1024UL : null;
    internal static bool HasValidRegisteredLocation(IReadOnlyList<string> rootPaths)
    {
        return rootPaths.Count == 0
            || rootPaths.Any(static path => Directory.Exists(path) || File.Exists(path));
    }

    private static IReadOnlyList<InstalledSoftwareEntry> NormalizeSharedInstallRoots(IEnumerable<InstalledSoftwareEntry> entries)
    {
        var normalizedEntries = entries.ToArray();
        var sharedInstallLocations = normalizedEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.InstallLocation))
            .GroupBy(static entry => NormalizePath(entry.InstallLocation), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Key is not null && group.Count() > 1 && group.Any(IsLauncherEntry))
            .Select(static group => group.Key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sharedInstallLocations.Count == 0)
        {
            return normalizedEntries;
        }

        return normalizedEntries
            .Select(entry =>
            {
                var installLocation = NormalizePath(entry.InstallLocation);
                if (installLocation is null
                    || !sharedInstallLocations.Contains(installLocation)
                    || IsLauncherEntry(entry))
                {
                    return entry;
                }

                var roots = entry.RootPaths
                    .Where(root => !installLocation.Equals(NormalizePath(root), StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return entry with { RootPaths = roots };
            })
            .ToArray();
    }

    private static bool IsLauncherEntry(InstalledSoftwareEntry entry)
    {
        return LooksLikeLauncherName(entry.Name)
            || LooksLikeLauncherCommand(entry.UninstallString);
    }

    private static bool LooksLikeLauncherName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("启动器", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Launcher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLauncherCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var executable = ExtractExecutablePathFromCommandLine(value);
        var fileName = Path.GetFileNameWithoutExtension(executable);
        return fileName is not null
            && (fileName.Equals("launcher", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            && !value.Contains("--uninstall_game", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    private static string? ReadString(RegistryKey key, string name, bool preserveEnvironmentVariables = false)
    {
        return key.GetValue(name, null, preserveEnvironmentVariables
            ? RegistryValueOptions.DoNotExpandEnvironmentNames
            : RegistryValueOptions.None) as string;
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    private static int? ReadInt(RegistryKey key, string name, bool preserveEnvironmentVariables = false)
    {
        return key.GetValue(name, null, preserveEnvironmentVariables
            ? RegistryValueOptions.DoNotExpandEnvironmentNames
            : RegistryValueOptions.None) switch
        {
            int value => value,
            string text when int.TryParse(text, out var value) => value,
            _ => null
        };
    }

    private static string? NormalizeInstallLocation(string? value, bool preserveEnvironmentVariables = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Trim().Trim('"');
            if (preserveEnvironmentVariables && normalized.Contains('%')) return null;
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private static IReadOnlyList<string> BuildRootPaths(
        string? installLocation,
        string? uninstallString,
        string? displayIcon,
        bool expandEnvironment = true)
    {
        var roots = new List<string>();
        AddRoot(roots, installLocation);

        var uninstallExecutable = ExtractExecutablePathFromCommandLine(uninstallString);
        var uninstallRoot = InferRootFromExecutablePath(uninstallExecutable, expandEnvironment);
        AddRoot(roots, uninstallRoot);

        var displayIconExecutable = ExtractExecutablePathFromCommandLine(displayIcon);
        var displayIconRoot = InferRootFromExecutablePath(displayIconExecutable, expandEnvironment);
        AddRoot(roots, displayIconRoot);

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRoot(List<string> roots, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null || IsWindowsOwnedPath(normalized))
        {
            return;
        }

        roots.Add(normalized);
    }

    internal static string? ExtractExecutablePathFromCommandLine(string? value)
    {
        return TrySplitExecutableCommandLine(value, out var fileName, out _)
            ? fileName
            : null;
    }

    internal static (string FileName, string Arguments) ParseExecutableCommandLine(string commandLine)
    {
        if (!TrySplitExecutableCommandLine(commandLine, out var fileName, out var arguments))
            throw new InvalidDataException("The registered uninstall command has no unambiguous executable path.");
        return (fileName, arguments);
    }

    private static bool TrySplitExecutableCommandLine(
        string? value,
        out string fileName,
        out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var command = value.Trim();
        var executableEnd = -1;

        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote <= 1) return false;
            fileName = command[1..closingQuote];
            arguments = command[(closingQuote + 1)..].TrimStart();
            return true;
        }

        var firstWhitespace = command.IndexOfAny([' ', '\t']);
        if (firstWhitespace > 0)
        {
            var firstToken = command[..firstWhitespace];
            if (!firstToken.Contains('\\') && !firstToken.Contains('/')
                && !Path.HasExtension(firstToken))
            {
                fileName = firstToken + ".exe";
                arguments = command[firstWhitespace..].TrimStart();
                return true;
            }
        }

        foreach (var extension in new[] { ".exe", ".msi", ".bat", ".cmd" })
        {
            var searchFrom = 0;
            while (searchFrom < command.Length)
            {
                var index = command.IndexOf(extension, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var end = index + extension.Length;
                if (end == command.Length || char.IsWhiteSpace(command[end]) || command[end] == ',')
                {
                    if (executableEnd < 0 || end < executableEnd) executableEnd = end;
                    break;
                }
                searchFrom = index + 1;
            }
        }

        if (executableEnd > 0)
        {
            fileName = command[..executableEnd].Trim();
            arguments = command[executableEnd..].TrimStart();
            return fileName.Length > 0;
        }

        if (firstWhitespace < 0 && !command.Contains('\\') && !command.Contains('/') && !string.IsNullOrWhiteSpace(command))
        {
            fileName = Path.HasExtension(command) ? command : command + ".exe";
            return true;
        }

        return false;
    }

    private static string? InferRootFromExecutablePath(string? executablePath, bool expandEnvironment)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || (!expandEnvironment && executablePath.Contains('%'))
            || !Path.IsPathFullyQualified(executablePath.Trim().Trim('"')))
        {
            return null;
        }

        var normalized = NormalizePath(executablePath, expandEnvironment);
        if (normalized is null)
        {
            return null;
        }

        var root = Path.HasExtension(normalized)
            ? Path.GetDirectoryName(normalized)
            : normalized;
        return NormalizePath(root);
    }

    private static string? NormalizePath(string? value, bool expandEnvironment = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Trim().Trim('"');
            if (!expandEnvironment && normalized.Contains('%')) return null;
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(normalized))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsWindowsOwnedPath(string path)
    {
        return WindowsRoot is not null
            && (path.Equals(WindowsRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(WindowsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static string BuildDeduplicationKey(InstalledSoftwareEntry entry)
    {
        var location = NormalizeKeyPart(entry.InstallLocation);
        var uninstall = NormalizeKeyPart(entry.UninstallString);
        var locationKey = !string.IsNullOrWhiteSpace(location) || !string.IsNullOrWhiteSpace(uninstall)
            ? $"{location}|{uninstall}"
            : "";
        return string.Join(
            "|",
            NormalizeKeyPart(entry.Name),
            NormalizeKeyPart(entry.Publisher),
            NormalizeKeyPart(entry.Version),
            locationKey);
    }

    private static bool PreferEntry(InstalledSoftwareEntry candidate, InstalledSoftwareEntry existing)
    {
        var candidateScore = EntryScore(candidate);
        var existingScore = EntryScore(existing);
        return candidateScore > existingScore;
    }

    private static int EntryScore(InstalledSoftwareEntry entry)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            score += 2;
        }

        if (entry.RegistryPath.Contains("LocalMachine", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        if (entry.RegistryPath.Contains("Registry64", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
    }

    private static string NormalizeKeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim().Trim('"').TrimEnd('\\', '/').ToLowerInvariant();
    }

    internal sealed record RegistryInventoryRoot(RegistryHive Hive, RegistryView View, string? UserSid);
}


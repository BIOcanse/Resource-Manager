using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Updates;

public sealed record ProductUpdateState(
    string Stage, string? TargetVersion, string? Workspace, string? Error, DateTimeOffset UpdatedAt)
{
    public string? ErrorCode { get; init; }
    public IReadOnlyDictionary<string, string>? ErrorParameters { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string? CheckResult { get; init; }
    public string? FailedVersion { get; init; }
    public string? CheckedInstalledVersion { get; init; }
    public IReadOnlyList<string> IncompatibleVersions { get; init; } = [];
}

public sealed class ProductUpdateFailure(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

public sealed record PreparedProductUpdate(string Version, string Workspace, string PackageRoot);

public sealed class ProductUpdateCoordinator
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly Func<CancellationToken, Task<ProductVersionCatalog>> readCatalog;
    private readonly Func<ProductVersionOption, CancellationToken, Task<PreparedProductUpdate>> prepare;
    private readonly Func<PreparedProductUpdate, CancellationToken, Task> launch;
    private readonly ProductUpdateStateStore store;
    private readonly TimeProvider clock;
    private readonly ILogger<ProductUpdateCoordinator> logger;
    private ProductUpdateState state;

    internal event Action? StageChanged;

    public ProductUpdateCoordinator(ProductVersionCatalogService catalog, IHttpClientFactory httpClientFactory,
        IHostEnvironment environment, TimeProvider clock, ILogger<ProductUpdateCoordinator> logger)
    {
        this.clock = clock;
        this.logger = logger;
        var root = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
        store = new ProductUpdateStateStore(root);
        readCatalog = catalog.ReadAsync;
        prepare = (option, token) => PreparePackageAsync(root, option, httpClientFactory, token);
        launch = (package, token) => LaunchManagerAsync(root, package, token);
        state = Restore();
    }

    // Same coordinator and durable state, with side effects supplied by the tests.
    internal ProductUpdateCoordinator(Func<CancellationToken, Task<ProductVersionCatalog>> readCatalog,
        Func<ProductVersionOption, CancellationToken, Task<PreparedProductUpdate>> prepare,
        Func<PreparedProductUpdate, CancellationToken, Task> launch,
        ProductUpdateStateStore store, TimeProvider clock, ILogger<ProductUpdateCoordinator> logger)
    {
        this.readCatalog = readCatalog;
        this.prepare = prepare;
        this.launch = launch;
        this.store = store;
        this.clock = clock;
        this.logger = logger;
        state = Restore();
    }

    private ProductUpdateState Restore()
    {
        var restored = store.Read() ?? new ProductUpdateState("idle", null, null, null, DateTimeOffset.MinValue);
        restored = store.ProjectOutcome(restored);
        if (restored.Stage is "checking" or "downloading" or "verifying")
            restored = Failure(restored, "update.preparationFailed");
        return restored;
    }

    public ProductUpdateState Status
    {
        get { lock (gate) return store.ProjectOutcome(state); }
    }

    private void Save(ProductUpdateState value)
        => Save(_ => value);

    private void Save(Func<ProductUpdateState, ProductUpdateState> update)
    {
        bool stageChanged;
        lock (gate)
        {
            var value = update(state);
            store.Write(value);
            stageChanged = state.Stage != value.Stage;
            state = value;
        }
        if (stageChanged) StageChanged?.Invoke();
    }

    private static bool Busy(ProductUpdateState value)
        => value.Stage is "checking" or "downloading" or "verifying" or "pendingApply" or "waitingForExit";

    public async Task<ProductVersionCatalog> CheckCatalogAsync(CancellationToken token)
    {
        var listing = await readCatalog(token);
        Save(current => store.ProjectOutcome(current) with
        {
            LastCheckedAt = clock.GetUtcNow(),
            CheckResult = listing.Status == "loaded" && listing.Complete ? "checked" : "failed"
        });
        return listing;
    }

    public ProductUpdateState Submit(string choice)
    {
        if (string.IsNullOrWhiteSpace(choice) || !choice.StartsWith("version:", StringComparison.Ordinal))
            throw new ProductUpdateFailure("update.choiceRequired");
        if (!operation.Wait(0)) throw new ProductUpdateFailure("update.busy");
        try
        {
            if (Busy(Status)) throw new ProductUpdateFailure("update.busy");
            Save(Status with { Stage = "checking", TargetVersion = choice[8..], Error = null, ErrorCode = null, UpdatedAt = clock.GetUtcNow() });
        }
        catch { operation.Release(); throw; }
        _ = Task.Run(async () =>
        {
            PreparedProductUpdate? prepared;
            try { prepared = await PrepareChoiceAsync(choice, CancellationToken.None); }
            finally { operation.Release(); }
            // The stage notification can immediately trigger automatic apply.
            // Make the operation gate available before announcing readiness.
            if (prepared is not null)
            {
                try { RecordPrepared(prepared); }
                catch (Exception exception) { RecordFailure(exception); }
            }
        });
        return Status;
    }

    private async Task<PreparedProductUpdate?> PrepareChoiceAsync(string choice, CancellationToken token)
    {
        try
        {
            var listing = await readCatalog(token);
            if (listing.Status != "loaded" || !listing.Complete) throw new ProductUpdateFailure("update.catalogUnavailable");
            var option = listing.Options.FirstOrDefault(item => item.Choice == choice && item.Selectable)
                ?? throw new ProductUpdateFailure("update.targetUnavailable");
            return await PrepareOptionAsync(option, token);
        }
        catch (Exception exception) { RecordFailure(exception); return null; }
    }

    private async Task<PreparedProductUpdate> PrepareOptionAsync(ProductVersionOption option, CancellationToken token)
    {
        Save(Status with { Stage = "downloading", TargetVersion = option.Version, Workspace = null,
            Error = null, ErrorCode = null, ErrorParameters = null, UpdatedAt = clock.GetUtcNow() });
        var prepared = await prepare(option, token);
        token.ThrowIfCancellationRequested();
        return prepared;
    }

    private void RecordPrepared(PreparedProductUpdate prepared)
    {
        Save(Status with { Stage = "pendingApply", TargetVersion = prepared.Version, Workspace = prepared.Workspace,
            Error = null, ErrorCode = null, UpdatedAt = clock.GetUtcNow() });
    }

    public async Task CheckAutomaticAsync(CancellationToken token)
    {
        if (!await operation.WaitAsync(0, token)) return;
        try
        {
            if (Busy(Status)) return;
            var checkedAt = clock.GetUtcNow();
            Save(Status with { LastCheckedAt = checkedAt, CheckResult = "checking" });
            var listing = await readCatalog(token);
            token.ThrowIfCancellationRequested();
            if (listing.Status != "loaded" || !listing.Complete)
            {
                Save(Status with { CheckResult = "failed", Error = "update.catalogUnavailable", ErrorCode = "update.catalogUnavailable", UpdatedAt = clock.GetUtcNow() });
                return;
            }
            var targets = AutomaticTargets(listing).ToArray();
            var installed = VersionIdentity(listing.InstalledVersion);
            var incompatible = Status.CheckedInstalledVersion == installed
                ? Status.IncompatibleVersions.Where(version => targets.Any(target => VersionIdentity(target.Version) == version)).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            Save(Status with { CheckResult = targets.Length == 0 ? "noUpdate" : "available",
                CheckedInstalledVersion = installed, IncompatibleVersions = incompatible.ToArray() });
            foreach (var target in targets)
            {
                var identity = VersionIdentity(target.Version)!;
                if (incompatible.Contains(identity)) continue;
                // Execution failures do not fall back to an older package. A schema
                // admission rejection, however, is not a compatible candidate.
                if (identity == VersionIdentity(Status.FailedVersion)) break;
                try { RecordPrepared(await PrepareOptionAsync(target, token)); break; }
                catch (UpdatePackageIncompatibleException exception)
                {
                    logger.LogInformation(exception, "Update {Version} is incompatible with this installation.", target.Version);
                    incompatible.Add(identity);
                    Save(Status with { Stage = "error", Error = "update.targetUnavailable", ErrorCode = "update.targetUnavailable",
                        IncompatibleVersions = incompatible.ToArray(), UpdatedAt = clock.GetUtcNow() });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            logger.LogInformation("Automatic preparation cancelled; retained workspace {Workspace}.", Status.Workspace);
            Save(Status with { Stage = "idle", TargetVersion = null, CheckResult = null,
                Error = null, ErrorCode = null, UpdatedAt = clock.GetUtcNow() });
            throw;
        }
        catch (Exception exception) { RecordFailure(exception); }
        finally { operation.Release(); }
    }

    internal static ProductVersionOption? SelectAutomaticTarget(ProductVersionCatalog listing)
        => AutomaticTargets(listing).FirstOrDefault();

    internal static string? VersionIdentity(string? version) => version?.TrimStart('v');

    private static IEnumerable<ProductVersionOption> AutomaticTargets(ProductVersionCatalog listing)
    {
        if (!ReleaseVersion.TryParse(listing.InstalledVersion, out var installed) || installed is null) return [];
        var channel = installed.Stable ? "stable" : "preview";
        return listing.Options.Where(option => option.Selectable && option.Channel == channel)
            .Select(option => (Option: option, Parsed: ReleaseVersion.TryParse(option.Version, out var parsed) ? parsed : null))
            .Where(item => item.Parsed is not null && item.Parsed.CompareTo(installed) > 0)
            .OrderByDescending(item => item.Parsed).Select(item => item.Option);
    }

    public async Task<ProductUpdateState> ApplyAsync(CancellationToken token)
    {
        if (!await operation.WaitAsync(0, token)) throw new ProductUpdateFailure("update.busy");
        try
        {
            var current = Status;
            if (current.Stage != "pendingApply" || current.TargetVersion is null || current.Workspace is null)
                throw new ProductUpdateFailure("update.targetUnavailable");
            var prepared = new PreparedProductUpdate(current.TargetVersion, current.Workspace, Path.Combine(current.Workspace, "package"));
            // Persist before launching: a manager can stop this process immediately.
            Save(current with { Stage = "waitingForExit", UpdatedAt = clock.GetUtcNow() });
            try { await launch(prepared, token); }
            catch (Exception exception) { RecordFailure(exception, "update.applyFailed"); }
            return Status;
        }
        finally { operation.Release(); }
    }

    private ProductUpdateState Failure(ProductUpdateState current, string code)
        => current with { Stage = "error", Error = code, ErrorCode = code,
            ErrorParameters = current.TargetVersion is null ? null : new Dictionary<string, string> { ["targetVersion"] = current.TargetVersion },
            FailedVersion = current.TargetVersion, UpdatedAt = clock.GetUtcNow() };

    private void RecordFailure(Exception exception, string fallback = "update.preparationFailed")
    {
        logger.LogError(exception, "Product update failed.");
        Save(Failure(Status, exception is ProductUpdateFailure failure ? failure.Code : fallback));
    }

    private async Task<PreparedProductUpdate> PreparePackageAsync(string root, ProductVersionOption option,
        IHttpClientFactory httpClientFactory, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new ProductUpdateFailure("update.serviceRequired");
        if (!File.Exists(Path.Combine(root, "release-manifest.json"))) throw new ProductUpdateFailure("update.packageRequired");
        _ = ResolveInstalledManager(root);
        var parent = Path.GetDirectoryName(root) ?? throw new ProductUpdateFailure("update.installRootUnavailable");
        var workspace = Path.Combine(parent, "ResourceManager-update-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Save(Status with { Workspace = workspace });
        using var client = httpClientFactory.CreateClient(ProductUpdateSource.HttpClientName);
        client.Timeout = TimeSpan.FromMinutes(10);
        var archivePath = Path.Combine(workspace, option.AssetName);
        var expected = await ReadChecksumAsync(client, option, token);
        await DownloadAndVerifyAsync(client, option.DownloadUrl, archivePath, expected, token);
        Save(Status with { Stage = "verifying", UpdatedAt = clock.GetUtcNow() });
        var packageRoot = Path.Combine(workspace, "package");
        ExtractChecked(archivePath, packageRoot);
        var package = ReleasePackageLayout.Verify(packageRoot);
        if (!string.Equals(package.Version.TrimStart('v'), option.Version.TrimStart('v'), StringComparison.Ordinal))
            throw new ProductUpdateFailure("update.versionMismatch");
        UpdatePackageCompatibility.RequireCompatible(packageRoot, root, databasePath =>
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        });
        // The updater repeats the same compatibility check before stopping the
        // service, since settings may change while a prepared package is pending.
        ProductUpdateDownload.RecordReady(packageRoot, root, package.Version);
        return new PreparedProductUpdate(option.Version, workspace, packageRoot);
    }

    private Task LaunchManagerAsync(string root, PreparedProductUpdate prepared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var package = ReleasePackageLayout.Verify(prepared.PackageRoot);
        if (package.Version.TrimStart('v') != prepared.Version.TrimStart('v')) throw new ProductUpdateFailure("update.versionMismatch");
        var updater = ResolveInstalledManager(root);
        var process = Process.Start(new ProcessStartInfo(updater)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = prepared.Workspace,
            ArgumentList = { "--apply", prepared.PackageRoot, root }
        }) ?? throw new ProductUpdateFailure("update.managerStartFailed");
        _ = ObserveAsync(process);
        return Task.CompletedTask;
    }

    private async Task ObserveAsync(Process process)
    {
        using (process)
        {
            try
            {
                await process.WaitForExitAsync();
                var current = Status;
                if (current.Stage == "waitingForExit" && process.ExitCode != 0)
                    Save(Failure(current, "update.applyFailed"));
            }
            catch (Exception exception) { logger.LogError(exception, "Updater observation failed."); }
        }
    }

    private static string ResolveInstalledManager(string installRoot)
    {
        var expected = UpdateManagerPaths.InstalledExecutable(installRoot);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\ResourceManager");
        if (key?.GetValue("InstallationContract") as string != "resource-manager-directory-registration-v1"
            || !string.Equals(key.GetValue("InstallRoot") as string, installRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(key.GetValue("UpdateManagerPath") as string, expected, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(expected)) throw new ProductUpdateFailure("update.managerUnavailable");
        ReleasePackageLayout.RejectReparse(UpdateManagerPaths.InstalledDirectory(installRoot));
        ReleasePackageLayout.RejectReparse(expected);
        return expected;
    }

    internal static async Task<string> ReadChecksumAsync(
        HttpClient client, ProductVersionOption option, CancellationToken cancellationToken)
    {
        using var response = await ProductUpdateSource.SendAssetAsync(client, option.ChecksumUrl, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = Regex.Match(text.Trim(), @"^([0-9a-fA-F]{64})\s\s([A-Za-z0-9._-]+)$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || match.Groups[2].Value != option.AssetName)
            throw new ProductUpdateFailure("update.checksumInvalid");
        return match.Groups[1].Value;
    }

    internal static async Task DownloadAndVerifyAsync(
        HttpClient client, string url, string path, string expected, CancellationToken cancellationToken)
    {
        using var response = await ProductUpdateSource.SendAssetAsync(client, url, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_147_483_648L)
            throw new ProductUpdateFailure("update.packageTooLarge");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > 2_147_483_648L) throw new ProductUpdateFailure("update.packageTooLarge");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ProductUpdateFailure("update.checksumMismatch");
    }

    private static void ExtractChecked(string archivePath, string target)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 50_000 || archive.Entries.Sum(entry => entry.Length) > 4_294_967_296L)
            throw new ProductUpdateFailure("update.packageTooLarge");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName;
            if (relative.Contains('\\') || relative.StartsWith('/')
                || relative.Split('/').Any(segment => segment is "." or "..")
                || !seen.Add(relative))
                throw new ProductUpdateFailure("update.archiveInvalid");
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new ProductUpdateFailure("update.archiveInvalid");
        }
        Directory.CreateDirectory(target);
        archive.ExtractToDirectory(target);
    }
}

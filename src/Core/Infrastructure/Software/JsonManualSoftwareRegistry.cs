using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Software;

public sealed partial class JsonManualSoftwareRegistry(
    IHostEnvironment environment,
    ILogger<JsonManualSoftwareRegistry>? logger = null) : IManualSoftwareRegistry
{
    public const int FormatVersion = 2;
    private sealed record RegistryDocument(int FormatVersion, IReadOnlyList<ManualSoftwareRecord?> Records);

    // One-time reader for files written by the pre-v2 manual registry. Delete this DTO,
    // its generated root and migration branch when no supported upgrade source writes
    // unversioned manual-software.json files. Backup retirement is a separate user-data decision.
    private sealed record LegacyManualSoftwareRecord(
        string Id, string Name, string Kind, string DisplayKind, string State,
        IReadOnlyList<string> Sources, IReadOnlyList<string> RootPaths, string Message,
        string? SourceSoftwareId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new JsonManualSoftwareRegistryJsonContext(),
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(RegistryDocument))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(LegacyManualSoftwareRecord[]))]
    private partial class JsonManualSoftwareRegistryJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<ManualSoftwareRecord>? cached;
    private OptionalFileVersion? cachedVersion;
    private bool unreadableSource;
    private bool metadataUnavailable;
    private readonly ILogger<JsonManualSoftwareRegistry> registryLogger = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonManualSoftwareRegistry>.Instance;
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "Config",
        "manual-software.json");

    public async Task<IReadOnlyList<ManualSoftwareRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManualSoftwareRecord> AddOrUpdateAsync(
        ManualSoftwareRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequest(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = (await LoadCoreAsync(cancellationToken)).ToList();
            var id = BuildId(normalized);
            var now = DateTimeOffset.Now;
            var existing = records.FirstOrDefault(record =>
                record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || HasSameSourceSoftware(record, normalized));
            var existingCreatedAt = existing?.CreatedAt ?? now;
            var record = new ManualSoftwareRecord(
                normalized.SourceSoftwareId ?? existing?.Id ?? id,
                normalized.Name,
                normalized.Kind,
                normalized.RootPaths,
                normalized.SourceSoftwareId,
                existingCreatedAt,
                now);
            records.RemoveAll(existingRecord =>
                existingRecord.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || existingRecord.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase)
                || HasSameSourceSoftware(existingRecord, normalized));
            records.Add(record);

            await SaveCoreAsync(records, cancellationToken);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = (await LoadCoreAsync(cancellationToken)).ToList();
            var removed = records.RemoveAll(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed || unreadableSource)
            {
                await SaveCoreAsync(records, cancellationToken);
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<ManualSoftwareRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        // Retry metadata even while its last failure is cached, so repairs are visible.
        if (!TryReadStorageVersion(out var version)) return cached!;
        if (metadataUnavailable)
        {
            metadataUnavailable = false;
            cached = null;
        }
        if (cached is not null && version == cachedVersion) return cached;
        if (version is null)
        {
            cachedVersion = null;
            unreadableSource = false;
            return cached = Array.Empty<ManualSoftwareRecord>();
        }
        unreadableSource = false;
        try
        {
            var value = await ReadCoreAsync(version.Value, cancellationToken);
            if (!TryReadStorageVersion(out var currentVersion)) return cached!;
            cachedVersion = currentVersion;
            return cached = value;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Reads publish an empty registry while preserving the unreadable file. Only
            // an explicit mutation may reset it, and that write must keep a byte-exact backup.
            unreadableSource = true;
            cachedVersion = version;
            registryLogger.LogWarning(error,
                "Manual software registry {RegistryPath} could not be read or migrated. Its source file is unchanged; an empty manual registry is in use. An explicit mutation will back up the original before resetting it.",
                storagePath);
            return cached = Array.Empty<ManualSoftwareRecord>();
        }
    }

    private bool TryReadStorageVersion(out OptionalFileVersion? version)
    {
        try
        {
            version = OptionalFileVersion.Read(storagePath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!metadataUnavailable)
                registryLogger.LogWarning(error,
                    "Manual software registry metadata {RegistryPath} could not be read. Its source file is unchanged; an empty manual registry is in use until metadata access recovers.",
                    storagePath);
            metadataUnavailable = true;
            unreadableSource = true;
            cachedVersion = null;
            cached = Array.Empty<ManualSoftwareRecord>();
            version = null;
            return false;
        }
    }

    private async Task<IReadOnlyList<ManualSoftwareRecord>> ReadCoreAsync(
        OptionalFileVersion version,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = File.OpenRead(storagePath);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        await using (stream)
        {
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                var document = json.RootElement.Deserialize<RegistryDocument>(JsonOptions);
                if (document is null || document.FormatVersion != FormatVersion || document.Records is null)
                    throw new InvalidDataException("Unsupported manual software registry document.");
                return ValidateStoredRecords(document.Records.Select(static record => record!));
            }

            var legacy = json.RootElement.Deserialize<LegacyManualSoftwareRecord?[]>(JsonOptions)
                ?? throw new InvalidDataException("The legacy manual software registry must contain an array.");
            var migrated = ValidateStoredRecords(legacy.Select(static record => record is null
                ? throw new InvalidDataException("The legacy manual software registry contains a null record.")
                : new ManualSoftwareRecord(record.Id, record.Name, SoftwareKinds.ReadStored(record.Kind),
                    record.RootPaths, record.SourceSoftwareId, record.CreatedAt, record.UpdatedAt)));
            // File.OpenRead denies replacement while held; release it before the owning
            // atomic writer creates the adjacent original-format backup and replaces it.
            await stream.DisposeAsync();
            if (!TryReadStorageVersion(out var currentVersion)) return cached!;
            if (currentVersion != version)
                throw new IOException("The manual software registry changed while its migration was being prepared.");
            await SaveCoreAsync(migrated, cancellationToken, migrateLegacy: true);
            return migrated;
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<ManualSoftwareRecord> records,
        CancellationToken cancellationToken,
        bool migrateLegacy = false)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            }))
            {
                await JsonSerializer.SerializeAsync(stream, new RegistryDocument(FormatVersion, ValidateStoredRecords(records)), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var backupPath = (migrateLegacy || unreadableSource) && File.Exists(storagePath)
                ? CreateOldFormatBackup()
                : null;
            File.Move(temporaryPath, storagePath, overwrite: true);
            cached = null;
            if (migrateLegacy)
                registryLogger.LogInformation("Migrated manual software registry {RegistryPath} to facts-only format {FormatVersion}; original retained at {BackupPath}.", storagePath, FormatVersion, backupPath);
            else if (unreadableSource)
                registryLogger.LogWarning("Explicit mutation reset the unreadable manual software registry {RegistryPath}; original retained at {BackupPath}.", storagePath, backupPath);
            unreadableSource = false;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string CreateOldFormatBackup()
    {
        var backupPath = storagePath + ".oldformatbackup";
        try
        {
            File.Copy(storagePath, backupPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            backupPath = storagePath + ".oldformatbackup." + Guid.NewGuid().ToString("N");
            File.Copy(storagePath, backupPath, overwrite: false);
        }
        return backupPath;
    }

    private static IReadOnlyList<ManualSoftwareRecord> ValidateStoredRecords(IEnumerable<ManualSoftwareRecord> records)
    {
        var materialized = records.ToArray();
        if (materialized.Any(static record => record is null
                || string.IsNullOrWhiteSpace(record.Id)
                || string.IsNullOrWhiteSpace(record.Name)))
            throw new InvalidDataException("The manual software registry contains a record without an ID or name; its contents were left unchanged.");
        if (materialized.Any(static record => !IsAllowedKind(record.Kind)))
            throw new InvalidDataException("The manual software registry contains an unsupported kind; its contents were left unchanged.");
        if (materialized.Any(static record => record.RootPaths is null || record.RootPaths.Any(static root => root is null)))
            throw new InvalidDataException("The manual software registry contains invalid root paths; its contents were left unchanged.");
        return materialized;
    }

    private static ManualSoftwareRequest NormalizeRequest(ManualSoftwareRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("A manual software classification request is required.");
        }

        var kind = request.Kind;
        if (!IsAllowedKind(kind))
        {
            throw new InvalidOperationException("Unsupported manual software kind.");
        }

        var roots = NormalizeRootPaths(request.RootPaths);
        var name = CleanText(request.Name)
            ?? (kind == SoftwareKinds.Adapted ? InferNameFromRootPath(roots.FirstOrDefault()) : null);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A software name is required.");
        }

        if (roots.Count == 0 && (kind == SoftwareKinds.Adapted
            || kind == SoftwareKinds.Other && string.IsNullOrWhiteSpace(request.SourceSoftwareId)))
        {
            throw new InvalidOperationException("At least one root path is required for this manual software registration.");
        }

        return new ManualSoftwareRequest(name, kind, roots, CleanText(request.SourceSoftwareId));
    }

    private static string BuildId(ManualSoftwareRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SourceSoftwareId)) return request.SourceSoftwareId;
        var source = $"{request.Name}|{string.Join("|", request.RootPaths)}";
        return $"manual:{ShortHash(source)}";
    }

    private static IReadOnlyList<string> NormalizeRootPaths(IEnumerable<string>? paths)
    {
        return (paths ?? [])
            .Select(CleanPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private static string? CleanText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string? InferNameFromRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return CleanText(leaf) ?? CleanText(path);
    }

    private static bool HasSameSourceSoftware(ManualSoftwareRecord record, ManualSoftwareRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.SourceSoftwareId)
            && !string.IsNullOrWhiteSpace(record.SourceSoftwareId)
            && record.SourceSoftwareId.Equals(request.SourceSoftwareId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedKind(string kind)
    {
        return kind is SoftwareKinds.Adapted or SoftwareKinds.Game or SoftwareKinds.HighPerformance or SoftwareKinds.Other;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}

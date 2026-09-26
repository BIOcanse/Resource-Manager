using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Settings;

public sealed class JsonDashboardSettingsStore : IDashboardSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<JsonDashboardSettingsStore>? logger;
    private readonly string settingsPath;
    private readonly string lastKnownGoodPath;
    private DashboardSettings? cachedSettings;
    private DateTimeOffset cachedUpdatedAt;
    private DashboardSettingsSourceMetadata? cachedSource;

    public JsonDashboardSettingsStore(
        IHostEnvironment environment,
        ILogger<JsonDashboardSettingsStore>? logger = null)
    {
        settingsPath = ResolveSettingsPath(environment.ContentRootPath);
        lastKnownGoodPath = ResolveLastKnownGoodPath(settingsPath);
        this.logger = logger;
    }

    public async Task<DashboardSettingsUpdateResult> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cachedSettings is not null)
            {
                return new DashboardSettingsUpdateResult(
                    cachedSettings,
                    cachedUpdatedAt,
                    settingsPath,
                    cachedSource ?? throw new InvalidOperationException(
                        "The cached dashboard settings source metadata is missing."));
            }

            var loaded = await LoadCoreAsync(cancellationToken);
            var updatedAt = File.Exists(settingsPath)
                ? File.GetLastWriteTimeUtc(settingsPath)
                : DateTimeOffset.UtcNow;
            cachedSettings = loaded.Settings;
            cachedUpdatedAt = updatedAt;
            cachedSource = loaded.Source;
            return new DashboardSettingsUpdateResult(
                loaded.Settings,
                updatedAt,
                settingsPath,
                loaded.Source);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DashboardSettingsUpdateResult> LoadReadOnlyAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadReadOnlyCoreAsync(cancellationToken);
            var updatedAt = File.Exists(settingsPath)
                ? File.GetLastWriteTimeUtc(settingsPath)
                : DateTimeOffset.UtcNow;
            return new DashboardSettingsUpdateResult(
                loaded.Settings,
                updatedAt,
                settingsPath,
                loaded.Source);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DashboardSettingsUpdateResult> SaveAsync(DashboardSettings settings, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var normalized = DashboardSettingsNormalizer.Normalize(settings);
            if (cachedSettings == normalized && File.Exists(settingsPath))
            {
                return new DashboardSettingsUpdateResult(
                    cachedSettings,
                    cachedUpdatedAt,
                    settingsPath,
                    cachedSource ?? CreateSavedSource(normalized));
            }

            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var committed = await SaveCoreAsync(normalized, cancellationToken);
            cachedSettings = normalized;
            cachedUpdatedAt = committed.UpdatedAt;
            cachedSource = committed.Source;

            return committed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LoadedSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            var recovered = await TryRecoverMissingPrimaryAsync(
                persist: true,
                cancellationToken);
            if (recovered is not null)
            {
                return recovered;
            }

            var defaults = DashboardSettingsDefaults.Create();
            var digest = ComputeEffectiveDigest(defaults);
            return new LoadedSettings(
                defaults,
                new DashboardSettingsSourceMetadata(
                    DashboardSettingsSourceKind.BundledFirstRun,
                    defaults.Version,
                    digest,
                    digest,
                    RewritePerformed: false));
        }

        var sourceBytes = await File.ReadAllBytesAsync(settingsPath, cancellationToken);
        try
        {
            var image = ParseSettingsImage(sourceBytes, settingsPath);
            await EnsureLastKnownGoodAsync(image.Settings, cancellationToken);
            return new LoadedSettings(
                image.Settings,
                new DashboardSettingsSourceMetadata(
                    DashboardSettingsSourceKind.Persisted,
                    image.SourceVersion,
                    image.InputSha256,
                    ComputeEffectiveDigest(image.Settings),
                    RewritePerformed: false)
                {
                    LastKnownGoodPath = lastKnownGoodPath
                });
        }
        catch (UnsupportedDashboardSettingsSchemaException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableSettingsCorruption(exception))
        {
            return await RecoverFromCorruptionAsync(
                sourceBytes,
                exception,
                cancellationToken);
        }
    }

    private async Task<LoadedSettings> LoadReadOnlyCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            var recovered = await TryRecoverMissingPrimaryAsync(
                persist: false,
                cancellationToken);
            if (recovered is not null)
            {
                return recovered;
            }

            var defaults = DashboardSettingsDefaults.Create();
            var digest = ComputeEffectiveDigest(defaults);
            return new LoadedSettings(
                defaults,
                new DashboardSettingsSourceMetadata(
                    DashboardSettingsSourceKind.BundledFirstRun,
                    defaults.Version,
                    digest,
                    digest,
                    RewritePerformed: false));
        }

        var sourceBytes = await File.ReadAllBytesAsync(settingsPath, cancellationToken);
        try
        {
            var image = ParseSettingsImage(sourceBytes, settingsPath);
            return new LoadedSettings(
                image.Settings,
                new DashboardSettingsSourceMetadata(
                    image.SourceVersion == DashboardSettingsDefaults.CurrentVersion
                        ? DashboardSettingsSourceKind.Persisted
                        : DashboardSettingsSourceKind.MigratedPersisted,
                    image.SourceVersion,
                    image.InputSha256,
                    ComputeEffectiveDigest(image.Settings),
                    RewritePerformed: false)
                {
                    LastKnownGoodPath = lastKnownGoodPath
                });
        }
        catch (UnsupportedDashboardSettingsSchemaException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableSettingsCorruption(exception))
        {
            return await RecoverReadOnlyFromCorruptionAsync(
                sourceBytes,
                exception,
                cancellationToken);
        }
    }

    private async Task<LoadedSettings> RecoverReadOnlyFromCorruptionAsync(
        byte[] corruptSourceBytes,
        Exception primaryFailure,
        CancellationToken cancellationToken)
    {
        if (File.Exists(lastKnownGoodPath))
        {
            var lastKnownGoodBytes = await File.ReadAllBytesAsync(
                lastKnownGoodPath,
                cancellationToken);
            try
            {
                var lastKnownGood = ParseSettingsImage(
                    lastKnownGoodBytes,
                    lastKnownGoodPath);
                var restored = DashboardSettingsNormalizer.Normalize(lastKnownGood.Settings);
                logger?.LogWarning(
                    primaryFailure,
                    "Dashboard settings are corrupt; a read-only last-known-good fallback is in use without modifying persisted files.");
                return new LoadedSettings(
                    restored,
                    new DashboardSettingsSourceMetadata(
                        DashboardSettingsSourceKind.RecoveredLastKnownGood,
                        lastKnownGood.SourceVersion,
                        lastKnownGood.InputSha256,
                        ComputeEffectiveDigest(restored),
                        RewritePerformed: false)
                    {
                        RecoveryDisposition = "readOnlyLastKnownGoodFallback",
                        LastKnownGoodPath = lastKnownGoodPath
                    });
            }
            catch (UnsupportedDashboardSettingsSchemaException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableSettingsCorruption(exception))
            {
                logger?.LogWarning(
                    exception,
                    "The dashboard settings last-known-good image is also corrupt; read-only defaults will be used.");
            }
        }

        var defaults = DashboardSettingsNormalizer.Normalize(
            DashboardSettingsDefaults.Create());
        logger?.LogWarning(
            primaryFailure,
            "Dashboard settings are corrupt; read-only defaults are in use without modifying persisted files.");
        return new LoadedSettings(
            defaults,
            new DashboardSettingsSourceMetadata(
                DashboardSettingsSourceKind.RecoveredDefaultsAfterCorruption,
                defaults.Version,
                Convert.ToHexString(SHA256.HashData(corruptSourceBytes)),
                ComputeEffectiveDigest(defaults),
                RewritePerformed: false)
            {
                RecoveryDisposition = "readOnlyDefaultsFallback",
                LastKnownGoodPath = lastKnownGoodPath
            });
    }

    private async Task<DashboardSettingsUpdateResult> SaveCoreAsync(
        DashboardSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var temporaryPath = $"{settingsPath}.{Guid.NewGuid():N}.tmp";
        var committed = false;
        try
        {
            await WriteDurableImageAsync(temporaryPath, payload, cancellationToken);
            await ValidateImageAsync(temporaryPath, payload, cancellationToken);
            var staged = new DashboardSettingsUpdateResult(
                settings,
                File.GetLastWriteTimeUtc(temporaryPath),
                settingsPath,
                CreateSavedSource(settings));

            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, settingsPath);
            committed = true;
            cachedSettings = null;
            cachedSource = null;
            await ValidateImageAsync(settingsPath, payload, cancellationToken);
            var committedUpdatedAt = new DateTimeOffset(
                File.GetLastWriteTimeUtc(settingsPath),
                TimeSpan.Zero);
            if (committedUpdatedAt != staged.UpdatedAt)
            {
                throw new InvalidDataException(
                    "The committed dashboard settings timestamp differs from the validated candidate.");
            }

            await WriteLastKnownGoodAsync(payload, cancellationToken);
            return staged;
        }
        catch (Exception exception) when (committed)
        {
            throw new DashboardSettingsCommitAmbiguousException(
                "The dashboard settings replacement committed, but strict post-commit validation failed.",
                exception);
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
    }

    private async Task<LoadedSettings> RecoverFromCorruptionAsync(
        byte[] corruptSourceBytes,
        Exception primaryFailure,
        CancellationToken cancellationToken)
    {
        ParsedSettingsImage? lastKnownGood = null;
        byte[]? lastKnownGoodBytes = null;
        Exception? lastKnownGoodFailure = null;
        if (File.Exists(lastKnownGoodPath))
        {
            lastKnownGoodBytes = await File.ReadAllBytesAsync(
                lastKnownGoodPath,
                cancellationToken);
            try
            {
                lastKnownGood = ParseSettingsImage(
                    lastKnownGoodBytes,
                    lastKnownGoodPath);
            }
            catch (UnsupportedDashboardSettingsSchemaException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableSettingsCorruption(exception))
            {
                lastKnownGoodFailure = exception;
            }
        }

        var quarantinePath = await CreateQuarantineArtifactAsync(
            settingsPath,
            "corrupt",
            corruptSourceBytes,
            cancellationToken);
        logger?.LogError(
            primaryFailure,
            "Dashboard settings were corrupt and quarantined at {QuarantinePath}.",
            quarantinePath);

        if (lastKnownGood is not null)
        {
            var restored = DashboardSettingsNormalizer.Normalize(lastKnownGood.Settings);
            _ = await SaveCoreAsync(restored, cancellationToken);
            logger?.LogWarning(
                "Dashboard settings were restored from last-known-good image {LastKnownGoodPath}.",
                lastKnownGoodPath);
            return new LoadedSettings(
                restored,
                new DashboardSettingsSourceMetadata(
                    DashboardSettingsSourceKind.RecoveredLastKnownGood,
                    lastKnownGood.SourceVersion,
                    lastKnownGood.InputSha256,
                    ComputeEffectiveDigest(restored),
                    lastKnownGood.SourceVersion != DashboardSettingsDefaults.CurrentVersion)
                {
                    RecoveryDisposition = "recoveredLastKnownGood",
                    RecoveryArtifactPath = quarantinePath,
                    LastKnownGoodPath = lastKnownGoodPath
                });
        }

        if (lastKnownGoodBytes is not null && lastKnownGoodFailure is not null)
        {
            var lastKnownGoodQuarantinePath = await CreateQuarantineArtifactAsync(
                lastKnownGoodPath,
                "last-good-corrupt",
                lastKnownGoodBytes,
                cancellationToken);
            logger?.LogError(
                lastKnownGoodFailure,
                "The last-known-good dashboard settings image was also corrupt and was quarantined at {QuarantinePath}.",
                lastKnownGoodQuarantinePath);
        }

        var defaults = DashboardSettingsNormalizer.Normalize(
            DashboardSettingsDefaults.Create());
        _ = await SaveCoreAsync(defaults, cancellationToken);
        logger?.LogWarning(
            "Dashboard settings were restored to safe defaults because no valid last-known-good image existed.");
        return new LoadedSettings(
            defaults,
            new DashboardSettingsSourceMetadata(
                DashboardSettingsSourceKind.RecoveredDefaultsAfterCorruption,
                defaults.Version,
                Convert.ToHexString(SHA256.HashData(corruptSourceBytes)),
                ComputeEffectiveDigest(defaults),
                RewritePerformed: true)
            {
                RecoveryDisposition = "recoveredDefaultsAfterCorruption",
                RecoveryArtifactPath = quarantinePath,
                LastKnownGoodPath = lastKnownGoodPath
            });
    }

    private async Task EnsureLastKnownGoodAsync(
        DashboardSettings settings,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        await WriteLastKnownGoodAsync(payload, cancellationToken);
    }

    private async Task<LoadedSettings?> TryRecoverMissingPrimaryAsync(
        bool persist,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(lastKnownGoodPath))
        {
            return null;
        }

        var sourceBytes = await File.ReadAllBytesAsync(lastKnownGoodPath, cancellationToken);
        ParsedSettingsImage image;
        try
        {
            image = ParseSettingsImage(sourceBytes, lastKnownGoodPath);
        }
        catch (UnsupportedDashboardSettingsSchemaException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableSettingsCorruption(exception))
        {
            logger?.LogWarning(
                exception,
                "The dashboard settings primary is missing and its last-known-good image is invalid; bundled defaults will be used.");
            return null;
        }

        var settings = DashboardSettingsNormalizer.Normalize(image.Settings);
        if (persist)
        {
            _ = await SaveCoreAsync(settings, cancellationToken);
        }

        logger?.LogWarning(
            "The dashboard settings primary was missing and was recovered from {LastKnownGoodPath}.",
            lastKnownGoodPath);
        return new LoadedSettings(
            settings,
            new DashboardSettingsSourceMetadata(
                DashboardSettingsSourceKind.RecoveredLastKnownGood,
                image.SourceVersion,
                image.InputSha256,
                ComputeEffectiveDigest(settings),
                RewritePerformed: persist)
            {
                RecoveryDisposition = persist
                    ? "recoveredMissingPrimaryFromLastKnownGood"
                    : "readOnlyMissingPrimaryLastKnownGoodFallback",
                LastKnownGoodPath = lastKnownGoodPath
            });
    }

    private async Task WriteLastKnownGoodAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (File.Exists(lastKnownGoodPath))
        {
            var existing = await File.ReadAllBytesAsync(lastKnownGoodPath, cancellationToken);
            if (existing.AsSpan().SequenceEqual(payload))
            {
                return;
            }
        }

        var temporaryPath = $"{lastKnownGoodPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteDurableImageAsync(temporaryPath, payload, cancellationToken);
            await ValidateImageAsync(temporaryPath, payload, cancellationToken);
            WindowsNativeAtomicFileCommitter.CommitReplace(
                temporaryPath,
                lastKnownGoodPath);
            await ValidateImageAsync(lastKnownGoodPath, payload, cancellationToken);
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
    }

    private static async Task WriteDurableImageAsync(
        string path,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task ValidateImageAsync(
        string path,
        byte[] expectedPayload,
        CancellationToken cancellationToken)
    {
        var committedPayload = await File.ReadAllBytesAsync(path, cancellationToken);
        var committed = ParseSettingsImage(committedPayload, path);
        var normalizedReadbackPayload = JsonSerializer.SerializeToUtf8Bytes(
            committed.Settings,
            JsonOptions);
        if (!normalizedReadbackPayload.AsSpan().SequenceEqual(expectedPayload))
        {
            throw new InvalidDataException(
                "The dashboard settings image failed strict readback validation.");
        }
    }

    private static ParsedSettingsImage ParseSettingsImage(
        byte[] sourceBytes,
        string sourcePath)
    {
        using var document = JsonDocument.Parse(sourceBytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var sourceVersion))
        {
            throw new InvalidDataException(
                $"The dashboard settings file '{sourcePath}' has no valid version identity.");
        }

        if (sourceVersion < DashboardSettingsDefaults.MinimumSupportedVersion
            || sourceVersion > DashboardSettingsDefaults.CurrentVersion)
        {
            throw new UnsupportedDashboardSettingsSchemaException(
                $"The dashboard settings file '{sourcePath}' uses unsupported schema '{sourceVersion}'.");
        }

        var deserialized = JsonSerializer.Deserialize<DashboardSettings>(
            sourceBytes,
            JsonOptions) ?? throw new InvalidDataException(
                $"The dashboard settings file '{sourcePath}' is empty.");
        if (deserialized.Version != sourceVersion)
        {
            throw new InvalidDataException(
                $"The dashboard settings file '{sourcePath}' has inconsistent version identity.");
        }

        return new ParsedSettingsImage(
            DashboardSettingsNormalizer.Normalize(
                deserialized,
                preserveVersion: true),
            sourceVersion,
            Convert.ToHexString(SHA256.HashData(sourceBytes)));
    }

    private DashboardSettingsSourceMetadata CreateSavedSource(
        DashboardSettings settings)
    {
        var digest = ComputeEffectiveDigest(settings);
        return new DashboardSettingsSourceMetadata(
            DashboardSettingsSourceKind.SavedPersisted,
            settings.Version,
            digest,
            digest,
            RewritePerformed: false)
        {
            LastKnownGoodPath = lastKnownGoodPath
        };
    }

    private static string ComputeEffectiveDigest(DashboardSettings settings)
        => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions)));

    private static bool IsRecoverableSettingsCorruption(Exception exception)
        => (exception is JsonException || exception is InvalidDataException)
            && exception is not UnsupportedDashboardSettingsSchemaException;

    private static async Task<string> CreateQuarantineArtifactAsync(
        string sourcePath,
        string disposition,
        byte[] sourceBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException(
                "The dashboard settings file has no parent directory.");
        var digest = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var quarantinePath = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}.{disposition}." +
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.{digest[..16]}." +
            $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
        var temporaryPath = $"{quarantinePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteDurableImageAsync(temporaryPath, sourceBytes, cancellationToken);
            var staged = await File.ReadAllBytesAsync(temporaryPath, cancellationToken);
            if (!staged.AsSpan().SequenceEqual(sourceBytes))
            {
                throw new InvalidDataException(
                    "The dashboard settings quarantine artifact failed strict readback validation.");
            }

            WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, quarantinePath);
            var committed = await File.ReadAllBytesAsync(quarantinePath, cancellationToken);
            if (!committed.AsSpan().SequenceEqual(sourceBytes))
            {
                throw new InvalidDataException(
                    "The committed dashboard settings quarantine artifact differs from the source bytes.");
            }

            return quarantinePath;
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
    }

    private static string ResolveSettingsPath(string contentRootPath)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(contentRootPath);

        return Path.Combine(packageRoot, "Config", "dashboard-settings.json");
    }

    private static string ResolveLastKnownGoodPath(string canonicalPath)
    {
        var directory = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The dashboard settings path has no parent directory.");
        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(canonicalPath)}.last-good" +
            Path.GetExtension(canonicalPath));
    }

    private sealed record LoadedSettings(
        DashboardSettings Settings,
        DashboardSettingsSourceMetadata Source);

    private sealed record ParsedSettingsImage(
        DashboardSettings Settings,
        int SourceVersion,
        string InputSha256);
}

internal sealed class DashboardSettingsCommitAmbiguousException(
    string message,
    Exception innerException) : IOException(message, innerException);

internal sealed class UnsupportedDashboardSettingsSchemaException(string message)
    : IOException(message);

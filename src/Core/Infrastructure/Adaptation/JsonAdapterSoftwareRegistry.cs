using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed class JsonAdapterSoftwareRegistry(IHostEnvironment environment) : IAdapterSoftwareRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "Config",
        "adapted-software.json");
    private IReadOnlyList<AdapterSoftwareRegistration>? current;

    public async Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return current ??= await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AdapterRegistrationResult> RegisterAsync(
        AdapterSoftwareRegistrationRequest request,
        AdapterResourceMarkerProbeResult markerProbe,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequest(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records =
                (current ??= await LoadCoreAsync(cancellationToken)).ToList();
            var id = BuildId(normalized);
            var now = DateTimeOffset.Now;
            var existing = records.FirstOrDefault(record =>
                record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || record.AdapterId.Equals(normalized.AdapterId, StringComparison.OrdinalIgnoreCase)
                || record.AppId.Equals(normalized.AppId, StringComparison.OrdinalIgnoreCase));
            var createdAt = existing?.CreatedAt ?? now;
            var registration = new AdapterSoftwareRegistration(
                id,
                AdapterRegistrationSchemaVersions.Current,
                normalized.AdapterId,
                normalized.AppId,
                normalized.DisplayName,
                normalized.Vendor,
                normalized.ProgramRootPaths,
                normalized.Processes,
                normalized.Services,
                normalized.ResourceMarkerEndpoint!,
                markerProbe,
                "registered",
                createdAt,
                now,
                normalized.SchedulingCapabilities);

            records.RemoveAll(record =>
                record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || record.AdapterId.Equals(normalized.AdapterId, StringComparison.OrdinalIgnoreCase)
                || record.AppId.Equals(normalized.AppId, StringComparison.OrdinalIgnoreCase));
            records.Add(registration);
            current = await SaveCoreAsync(records, cancellationToken);
            return new AdapterRegistrationResult(registration, existing is null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records =
                (current ??= await LoadCoreAsync(cancellationToken)).ToList();
            var removed = records.RemoveAll(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                current = await SaveCoreAsync(records, cancellationToken);
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<AdapterSoftwareRegistration>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storagePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(storagePath);
            var document = await JsonSerializer.DeserializeAsync<AdapterSoftwareRegistryDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return NormalizeRecords(document?.Registrations ?? []);
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<AdapterSoftwareRegistration>> SaveCoreAsync(
        IReadOnlyList<AdapterSoftwareRegistration> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = NormalizeRecords(records);
        var document = new AdapterSoftwareRegistryDocument(
            AdapterRegistrationSchemaVersions.Current,
            normalized);
        await using var stream = File.Create(storagePath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return normalized;
    }

    private static AdapterSoftwareRegistrationRequest NormalizeRequest(AdapterSoftwareRegistrationRequest request)
    {
        return request with
        {
            SchemaVersion = CleanRequiredText(request.SchemaVersion),
            AdapterId = CleanRequiredText(request.AdapterId),
            AppId = CleanRequiredText(request.AppId),
            DisplayName = CleanRequiredText(request.DisplayName),
            Vendor = CleanOptionalText(request.Vendor),
            ProgramRootPaths = NormalizeRootPaths(request.ProgramRootPaths),
            Processes = NormalizeProcesses(request.Processes),
            Services = NormalizeServices(request.Services),
            SchedulingCapabilities = request.SchedulingCapabilities?.Normalize(),
            ResourceMarkerEndpoint = request.ResourceMarkerEndpoint is null
                ? null
                : new AdapterResourceMarkerEndpoint(
                    CleanRequiredText(request.ResourceMarkerEndpoint.Transport).ToLowerInvariant(),
                    CleanRequiredText(request.ResourceMarkerEndpoint.Address))
        };
    }

    private static IReadOnlyList<AdapterSoftwareRegistration> NormalizeRecords(IEnumerable<AdapterSoftwareRegistration> records)
    {
        return records
            .Where(static record =>
                !string.IsNullOrWhiteSpace(record.Id)
                && !string.IsNullOrWhiteSpace(record.AdapterId)
                && !string.IsNullOrWhiteSpace(record.AppId)
                && !string.IsNullOrWhiteSpace(record.DisplayName)
                && string.Equals(record.SchemaVersion, AdapterRegistrationSchemaVersions.Current, StringComparison.Ordinal)
                && record.ResourceMarkerEndpoint is not null
                && AdapterRegistrationValidator.ValidateResourceMarkerEndpoint(record.ResourceMarkerEndpoint) is null)
            .Select(static record => record with
            {
                Vendor = CleanOptionalText(record.Vendor),
                ProgramRootPaths = NormalizeRootPaths(record.ProgramRootPaths),
                Processes = NormalizeProcesses(record.Processes),
                Services = NormalizeServices(record.Services),
                LastResourceMarkerProbe = record.LastResourceMarkerProbe ?? new AdapterResourceMarkerProbeResult(
                    AdapterResourceMarkerStates.Unsupported,
                    DateTimeOffset.MinValue,
                    null,
                    "Missing resource marker probe state."),
                SchedulingCapabilities = record.SchedulingCapabilities?.Normalize(),
                State = string.IsNullOrWhiteSpace(record.State) ? "registered" : record.State.Trim()
            })
            .Where(static record => record.ProgramRootPaths.Count > 0)
            .GroupBy(static record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static record => record.UpdatedAt).First())
            .OrderBy(static record => record.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static IReadOnlyList<AdapterProcessDeclaration> NormalizeProcesses(IEnumerable<AdapterProcessDeclaration>? processes)
    {
        return (processes ?? [])
            .Where(static process =>
                !string.IsNullOrWhiteSpace(process.Name)
                || process.ProcessId is not null
                || !string.IsNullOrWhiteSpace(process.ExecutablePath))
            .Select(static process => process with
            {
                Name = CleanOptionalText(process.Name) ?? string.Empty,
                ExecutablePath = CleanPath(process.ExecutablePath)
            })
            .ToArray();
    }

    private static IReadOnlyList<AdapterServiceDeclaration> NormalizeServices(IEnumerable<AdapterServiceDeclaration>? services)
    {
        return (services ?? [])
            .Where(static service => !string.IsNullOrWhiteSpace(service.Name))
            .Select(static service => service with
            {
                Name = CleanRequiredText(service.Name),
                DisplayName = CleanOptionalText(service.DisplayName)
            })
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

    private static string CleanRequiredText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string? CleanOptionalText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string BuildId(AdapterSoftwareRegistrationRequest request)
    {
        return $"adapter:{ShortHash($"{request.AdapterId}|{request.AppId}")}";
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private sealed record AdapterSoftwareRegistryDocument(
        string SchemaVersion,
        IReadOnlyList<AdapterSoftwareRegistration> Registrations);
}

using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed class JsonGpuLaunchExecutionReportStore(IHostEnvironment environment)
    : IGpuLaunchExecutionReportStore
{
    private const int MaxReports = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData",
        "SoftwareProfiles",
        "gpu-launch-results.local.json");

    public async Task<IReadOnlyList<GpuLaunchExecutionReport>> GetLatestAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadCoreAsync(cancellationToken)).Reports;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GpuLaunchExecutionReport> RecordAsync(
        GpuLaunchExecutionReport report,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeReport(report, DateTimeOffset.UtcNow);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var reports = document.Reports
                .Where(existing => !existing.ExecutablePath.Equals(
                    normalized.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.OccurredAt)
                .Take(MaxReports)
                .ToArray();
            await SaveCoreAsync(new GpuLaunchExecutionReportDocument(
                1,
                reports,
                normalized.OccurredAt), cancellationToken);
            return normalized;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GpuLaunchExecutionReportDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storagePath))
        {
            return GpuLaunchExecutionReportDocument.Empty;
        }

        try
        {
            await using var stream = File.OpenRead(storagePath);
            var document = await JsonSerializer.DeserializeAsync<GpuLaunchExecutionReportDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return NormalizeDocument(document);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return GpuLaunchExecutionReportDocument.Empty;
        }
    }

    private async Task SaveCoreAsync(
        GpuLaunchExecutionReportDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storagePath)
            ?? throw new IOException("GPU launch report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{storagePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, storagePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static GpuLaunchExecutionReportDocument NormalizeDocument(
        GpuLaunchExecutionReportDocument? document)
    {
        if (document is null)
        {
            return GpuLaunchExecutionReportDocument.Empty;
        }

        var reports = (document.Reports ?? [])
            .Select(report => TryNormalizeReport(report, report.OccurredAt))
            .OfType<GpuLaunchExecutionReport>()
            .GroupBy(static report => report.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static report => report.OccurredAt).First())
            .OrderByDescending(static report => report.OccurredAt)
            .Take(MaxReports)
            .ToArray();
        return new GpuLaunchExecutionReportDocument(1, reports, document.UpdatedAt);
    }

    private static GpuLaunchExecutionReport NormalizeReport(
        GpuLaunchExecutionReport report,
        DateTimeOffset occurredAt)
    {
        return TryNormalizeReport(report, occurredAt)
            ?? throw new InvalidOperationException("GPU 启动执行回执无效。");
    }

    private static GpuLaunchExecutionReport? TryNormalizeReport(
        GpuLaunchExecutionReport? report,
        DateTimeOffset occurredAt)
    {
        if (report is null
            || string.IsNullOrWhiteSpace(report.ExecutablePath)
            || !GpuLaunchExecutionOutcomes.Known.Contains(report.Outcome))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(report.ExecutablePath.Trim());
            return report with
            {
                ExecutablePath = path,
                SoftwareId = Clean(report.SoftwareId, 256),
                ProcessKey = Clean(report.ProcessKey, 512),
                ProcessId = report.ProcessId is > 0 ? report.ProcessId : null,
                Outcome = report.Outcome.Trim().ToLowerInvariant(),
                Message = Clean(report.Message, 1024) ?? string.Empty,
                StartupTargetGpu = Clean(report.StartupTargetGpu, 128),
                AssignedPositionId = Clean(report.AssignedPositionId, 128),
                TargetAdapterName = Clean(report.TargetAdapterName, 512),
                OccurredAt = occurredAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : occurredAt
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? Clean(string? value, int maxLength)
    {
        var clean = value?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}

using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.RuntimeSpecialization;
using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private async Task<ProcessAttributionSnapshot> CreateProcessAttributionSnapshotAsync(
        IReadOnlyList<ProcessResourceSample> processSamples,
        CancellationToken cancellationToken)
    {
        var processById = processSamples.ToDictionary(static process => process.ProcessId);
        var baseScorePlan = runtimePlanProvider.Current.BaseScore;
        var capture = await RuntimeProcessAttributionCapture.CaptureAsync(processAttributionCatalogProvider,
            processSamples.Select(CreateRuntimeIdentity).ToArray(), baseScorePlan, cancellationToken);
        var batch = capture.Batch;
        if (batch.Failures.Count != 0)
            logger?.LogWarning("Process attribution could not observe these processes: {FailedProcesses}.",
                string.Join(", ", batch.Failures.Select(pair => $"{pair.Key}[{pair.Value}]")));
        return new ProcessAttributionSnapshot(
            processSamples, processById, batch.SoftwareByProcessId,
            baseScorePlan, capture.SoftwareBaseScores, batch.HelperOwnerByProcessId);
    }

    private RuntimeProcessIdentity CreateRuntimeIdentity(ProcessResourceSample process)
    {
        var identity = new RuntimeProcessIdentity(
            process.ProcessId,
            process.ParentProcessId,
            process.Name,
            process.ExecutablePath,
            process.IsSelfDescendant,
            process.FileDescription,
            process.ProductName,
            process.CompanyName,
            process.ApplicationUserModelId,
            process.WindowApplicationUserModelId,
            process.WindowTitle,
            process.StartKey,
            UserSid: process.BasicMetrics.UserSid);
        if (process.StartKey is not > 0) return identity;
        var key = new ProcessInstanceKey(process.ProcessId, process.StartKey.Value);
        if (processAttributionCache.TryGet(key, out var cached) && cached is not null)
            return identity with { CommandLine = cached.Identity.CommandLine };
        identity = identity with { CommandLine = ReadHelperCommandLine(process) };
        if (identity.CommandLine is not null)
            processAttributionCache.Set(key, new CachedProcessAttribution(identity));
        return identity;
    }

    private static string? ReadHelperCommandLine(ProcessResourceSample process)
    {
        if (process.StartKey is not > 0) return null;
        return new ResourceManager.ReadWrite.Windows.Processes.WindowsProcessBasicMetricReader().Read(
            process.ProcessId, process.StartKey.Value,
            ResourceManager.ReadWrite.ProcessFields.ProcessMetricReadMask.CommandLine).CommandLine;
    }

    private sealed record ProcessAttributionSnapshot(
        IReadOnlyList<ProcessResourceSample> Processes,
        IReadOnlyDictionary<int, ProcessResourceSample> ProcessById,
        IReadOnlyDictionary<int, RuntimeSoftwareAttribution> AttributionByProcessId,
        CompiledBaseScorePlan BaseScorePlan,
        ImmutableArray<SoftwareBaseScore> SoftwareBaseScores,
        IReadOnlyDictionary<int, int>? HelperOwnerByProcessId = null);

    private sealed record CachedProcessAttribution(
        RuntimeProcessIdentity Identity);
}

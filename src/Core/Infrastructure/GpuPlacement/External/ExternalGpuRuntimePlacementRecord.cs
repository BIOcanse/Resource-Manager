using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal static class ExternalGpuRuntimePlacementRecord
{
    internal const string ResultKey = "runtimeActionResult";
    internal const string SettledKey = "runtimeActionSettled";
    private const string VersionKey = "externalRuntimeVersion";
    private const string IdPrefix = "external-runtime:";

    internal static HostManagerAppliedRecord Create(int processId, ulong processStartKey,
        Dictionary<string, string> metadata)
    {
        metadata[VersionKey] = "1";
        metadata[SettledKey] = "false";
        metadata[ResultKey] = JsonSerializer.Serialize(new RunningGpuPlacementActionResult(
            [], "External action has not settled.", RunningGpuPlacementActionStatuses.Unresolved));
        return new(HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger,
            string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{processId}:{processStartKey}"), metadata);
    }

    internal static bool IsRecord(HostManagerAppliedRecord record)
        => record.Kind == HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger
            && record.RecordId.StartsWith(IdPrefix, StringComparison.Ordinal)
            && record.Metadata?.GetValueOrDefault(VersionKey) == "1"
            && record.Metadata.TryGetValue("processId", out var pid)
            && record.Metadata.TryGetValue("processStartKey", out var birth)
            && record.RecordId == IdPrefix + pid + ":" + birth;

    internal static bool IsMutableResultField(string key)
        => key is ResultKey or SettledKey;

    internal static bool TryReadResult(HostManagerAppliedRecord record,
        out RunningGpuPlacementActionResult? result)
    {
        result = null;
        if (!IsRecord(record) || record.Metadata?.GetValueOrDefault(SettledKey) != "true"
            || !record.Metadata.TryGetValue(ResultKey, out var json)) return false;
        try
        {
            result = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(json);
            return result is not null;
        }
        catch (JsonException) { return false; }
    }

    internal static bool IsConfirmed(RunningGpuPlacementActionResult result)
    {
        if (result.Status != RunningGpuPlacementActionStatuses.RecreateRequested
            || result.Records.Count != 1) return false;
        var facts = result.Records[0].Metadata;
        return facts.GetValueOrDefault("controllerExited") == bool.TrueString
            && facts.GetValueOrDefault("cleanupPassed") == bool.TrueString
            && facts.GetValueOrDefault("controllerSucceeded") == bool.TrueString
            && facts.GetValueOrDefault("residentConfirmation") is "confirmed" or "partial";
    }
}

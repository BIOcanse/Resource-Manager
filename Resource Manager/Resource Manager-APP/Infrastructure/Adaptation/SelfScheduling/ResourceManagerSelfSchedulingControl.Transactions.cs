using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerSelfSchedulingControl
{
    private const uint SchedulingPayloadVersion = 1;
    private static readonly JsonSerializerOptions SchedulingPayloadJson = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private readonly AdapterSchedulingStateIdentity schedulingIdentity = CreateSchedulingIdentity();

    public AdapterSchedulingStateExportResult ExportCoordinatorSchedulingState(
        int maximumPayloadBytes,
        DateTimeOffset deadline)
    {
        lock (schedulingGate)
        {
            if (deadline <= DateTimeOffset.UtcNow || maximumPayloadBytes <= 0)
            {
                return ExportFailure("The self scheduling export deadline or payload limit is invalid.");
            }

            if (!InternalCpuConsumersMatch(currentCpuGrade))
            {
                return ExportFailure("The actual self CPU consumers have not reached the current requested state.",
                    AdapterSchedulingStateExportStatus.Unavailable);
            }

            var state = new CoordinatorSchedulingState(
                currentCpuGrade,
                currentGpuGrade,
                schedulingSources.Values.Where(IsCoordinatorSource)
                    .OrderBy(static source => source.TargetId, StringComparer.OrdinalIgnoreCase).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SchedulingPayloadJson);
            if (bytes.Length > maximumPayloadBytes)
            {
                return ExportFailure("The self scheduling state exceeds the explicit payload limit.");
            }

            return new AdapterSchedulingStateExportResult(
                AdapterSchedulingStateExportStatus.Exported,
                schedulingIdentity,
                new AdapterSchedulingStatePayload(SchedulingPayloadVersion,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes),
                ResourceManagerSelfCpuGrades.ToAdapter(currentCpuGrade),
                ResourceManagerSelfGpuGrades.ToAdapter(currentGpuGrade),
                DateTimeOffset.UtcNow,
                "Exported the current self scheduling owner and coordinator sources.");
        }
    }

    public AdapterSchedulingStateRestoreResult RestoreCoordinatorSchedulingState(
        AdapterSchedulingStateRestoreCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (schedulingGate)
        {
            if (!Equals(command.Identity, schedulingIdentity))
            {
                return RestoreFailure(AdapterSchedulingStateOwnershipResult.OwnershipLost,
                    "The captured state belongs to another self scheduling owner.");
            }

            if (command.Deadline <= DateTimeOffset.UtcNow
                || !TryReadCoordinatorState(command, out var original))
            {
                return RestoreFailure(AdapterSchedulingStateOwnershipResult.NotEvaluated,
                    "The self scheduling restore payload or deadline is invalid.");
            }

            var candidate = new Dictionary<string, ResourceManagerSelfSchedulingSource>(
                schedulingSources, StringComparer.OrdinalIgnoreCase);
            foreach (var source in schedulingSources.Values.Where(IsCoordinatorSource))
            {
                candidate.Remove(source.TargetId);
            }
            foreach (var source in original.Sources)
            {
                if (!candidate.TryAdd(source.TargetId, source))
                {
                    return RestoreFailure(AdapterSchedulingStateOwnershipResult.OwnershipLost,
                        "Another caller now owns a captured scheduling source.");
                }
            }

            var (cpuGrade, gpuGrade) = AggregateSchedulingGrades(candidate.Values);
            if (cpuGrade != original.CpuGrade || gpuGrade != original.GpuGrade)
            {
                return RestoreFailure(AdapterSchedulingStateOwnershipResult.NotEvaluated,
                    "Other callers changed the captured aggregate state; their sources were preserved.");
            }

            var sourcesChanged = candidate.Count != schedulingSources.Count
                || candidate.Any(pair => !schedulingSources.TryGetValue(pair.Key, out var value)
                    || value != pair.Value);
            // Restore the actual consumers before publishing a restored logical state.
            var consumersChanged = ApplyInternalCpuGrade(cpuGrade);
            if (sourcesChanged)
            {
                schedulingSources.Clear();
                foreach (var pair in candidate)
                {
                    schedulingSources.Add(pair.Key, pair.Value);
                }
            }
            UpdateSchedulingGradesLocked(cpuGrade, gpuGrade, AdapterSchedulingPolicyIds.HostManager,
                "Restored the captured coordinator-owned self scheduling sources.");

            return new AdapterSchedulingStateRestoreResult(
                AdapterSchedulingStateRestoreStatus.Restored,
                sourcesChanged || consumersChanged
                    ? AdapterSchedulingStateOwnershipResult.Restored
                    : AdapterSchedulingStateOwnershipResult.AlreadyRestored,
                schedulingIdentity,
                ResourceManagerSelfCpuGrades.ToAdapter(currentCpuGrade),
                ResourceManagerSelfGpuGrades.ToAdapter(currentGpuGrade),
                DateTimeOffset.UtcNow,
                "Restored self scheduling without replacing another caller's sources.");
        }
    }

    private static bool TryReadCoordinatorState(
        AdapterSchedulingStateRestoreCommand command,
        out CoordinatorSchedulingState state)
    {
        state = null!;
        var payload = command.Payload;
        if (payload is null || payload.Version != SchedulingPayloadVersion
            || payload.Bytes is not { Length: > 0 } bytes
            || bytes.Length > command.MaximumPayloadBytes
            || !string.Equals(payload.Sha256Digest, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<CoordinatorSchedulingState>(bytes, SchedulingPayloadJson);
            if (decoded is null || decoded.Sources is null
                || !Enum.IsDefined(decoded.CpuGrade) || !Enum.IsDefined(decoded.GpuGrade)
                || ResourceManagerSelfCpuGrades.ToAdapter(decoded.CpuGrade) != command.ExpectedCpuGrade
                || ResourceManagerSelfGpuGrades.ToAdapter(decoded.GpuGrade) != command.ExpectedGpuGrade)
            {
                return false;
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in decoded.Sources)
            {
                if (source is null || !IsCoordinatorSource(source)
                    || string.IsNullOrWhiteSpace(source.TargetId) || !targets.Add(source.TargetId)
                    || !Enum.IsDefined(source.CpuGrade) || !Enum.IsDefined(source.GpuGrade)
                    || (source.CpuGrade == ResourceManagerSelfCpuGrade.Normal
                        && source.GpuGrade == ResourceManagerSelfGpuGrade.Normal))
                {
                    return false;
                }
            }
            state = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private AdapterSchedulingStateExportResult ExportFailure(string message,
        AdapterSchedulingStateExportStatus status = AdapterSchedulingStateExportStatus.Conflict) => new(
        status, schedulingIdentity, null, null, null,
        DateTimeOffset.UtcNow, message);

    private AdapterSchedulingStateRestoreResult RestoreFailure(
        AdapterSchedulingStateOwnershipResult ownership, string message) => new(
        AdapterSchedulingStateRestoreStatus.Conflict, ownership, schedulingIdentity,
        ResourceManagerSelfCpuGrades.ToAdapter(currentCpuGrade),
        ResourceManagerSelfGpuGrades.ToAdapter(currentGpuGrade), DateTimeOffset.UtcNow, message);

    private static bool IsCoordinatorSource(ResourceManagerSelfSchedulingSource source) =>
        string.Equals(source.PolicyId, AdapterSchedulingPolicyIds.HostManager, StringComparison.Ordinal);

    private static AdapterSchedulingStateIdentity CreateSchedulingIdentity()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(sizeof(ulong)));
        // An in-process owner has one lifetime, not an external renewable registration.
        return new AdapterSchedulingStateIdentity(new(high, low), new(high, low), 1,
            RuntimeAttributionIds.ResourceManagerSelf, RuntimeAttributionIds.ResourceManagerSelf,
            RuntimeAttributionIds.ResourceManagerSelf);
    }

    private sealed record CoordinatorSchedulingState(
        ResourceManagerSelfCpuGrade CpuGrade,
        ResourceManagerSelfGpuGrade GpuGrade,
        ResourceManagerSelfSchedulingSource[] Sources);
}

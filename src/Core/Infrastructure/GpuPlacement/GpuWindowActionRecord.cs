using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal sealed record GpuWindowActionExitSettlement(
    DateTimeOffset ObservedAt,
    RecoveryReadResult<ProcessInstanceRecoverySnapshot> Target,
    RecoveryReadResult<ProcessInstanceRecoverySnapshot> Worker);

internal sealed record GpuWindowActionRecord(GpuWindowActionPrepared Prepared, GpuWindowActionResult? Result,
    GpuWindowActionExitSettlement? ExitSettlement = null)
{
    private const string IdPrefix = "gpu-window-action:";
    private const string VersionKey = "windowActionVersion";
    private const string PayloadKey = "windowActionPayload";
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false
    };

    internal bool BlocksAutomaticAction => ExitSettlement is null && (Result is null || Result.PersistencePending
        || !Result.Cleanup.Complete || Result.Cleanup.TerminationRequested
        || Result.Outcome is GpuWindowActionOutcome.Unresolved or GpuWindowActionOutcome.RestorationUnconfirmed);

    internal static bool IsActionFact(HostManagerAppliedRecord record)
        => record.Kind.Equals(HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger, StringComparison.OrdinalIgnoreCase)
            && (record.RecordId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)
                || record.Metadata?.ContainsKey(VersionKey) == true || record.Metadata?.ContainsKey(PayloadKey) == true);

    internal static HostManagerAppliedRecord Create(GpuWindowActionPrepared prepared, GpuWindowActionResult? result = null)
        => Encode(new(prepared, result));

    private static HostManagerAppliedRecord Encode(GpuWindowActionRecord fact)
    {
        fact.Validate();
        return new(HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger, CreateId(fact.Prepared),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [VersionKey] = "1",
                [PayloadKey] = JsonSerializer.Serialize(fact, Json)
            });
    }

    internal static HostManagerAppliedRecord Complete(HostManagerAppliedRecord pending, GpuWindowActionResult result)
    {
        if (!TryRead(pending, out var previous) || previous.Result is not null || previous.ExitSettlement is not null)
            throw new InvalidDataException("Only the original pending window action can receive its result.");
        return Create(previous.Prepared, result);
    }

    internal HostManagerAppliedRecord SettleExited(GpuWindowActionExitSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (!BlocksAutomaticAction)
            throw new InvalidOperationException("Only an unsettled window action can be settled after process exit.");
        return Encode(this with { ExitSettlement = settlement });
    }

    internal static bool TryRead(HostManagerAppliedRecord record,
        [NotNullWhen(true)] out GpuWindowActionRecord? fact)
    {
        fact = null;
        if (!IsActionFact(record) || record.Metadata is not { } metadata
            || !metadata.TryGetValue(VersionKey, out var version) || version != "1"
            || !metadata.TryGetValue(PayloadKey, out var payload) || payload is null) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<GpuWindowActionRecord>(payload, Json);
            if (parsed is null) return false;
            parsed.Validate();
            if (!string.Equals(record.RecordId, CreateId(parsed.Prepared), StringComparison.Ordinal)) return false;
            fact = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    internal static bool BlocksProcess(IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        string targetId, int processId, ulong creationFileTimeUtc)
    {
        foreach (var placement in placements)
        {
            foreach (var record in placement.Records)
            {
                if (!IsActionFact(record)) continue;
                if (!TryRead(record, out var fact))
                {
                    if (placement.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }
                var target = fact.Prepared.Window.Request;
                if (target.ProcessId == processId && checked((ulong)target.CreationFileTimeUtc) == creationFileTimeUtc
                    && fact.BlocksAutomaticAction) return true;
            }
        }
        return false;
    }

    private static string CreateId(GpuWindowActionPrepared prepared)
        => string.Create(CultureInfo.InvariantCulture,
            $"{IdPrefix}{prepared.Window.Request.ProcessId}:{prepared.Window.Request.CreationFileTimeUtc}:{prepared.Worker.ProcessId}:{prepared.Worker.CreationFileTimeUtc}:{prepared.Window.Request.Window:x16}");

    private void Validate()
    {
        if (Prepared?.Window?.Request is not { } request || Prepared.Window.Before is not { } before
            || Prepared.Worker is not { } worker)
            throw new InvalidDataException("A window fact requires the actual preparation and worker.");
        request.Validate();
        if (Prepared.Window.WindowThreadId == 0 || !before.Visible || before.Iconic
            || before.Width <= 0 || before.Width == int.MaxValue || before.Height <= 0
            || worker.ProcessId <= 4 || worker.CreationFileTimeUtc <= 0
            || string.IsNullOrWhiteSpace(worker.ExecutablePath) || !Path.IsPathFullyQualified(worker.ExecutablePath))
            throw new InvalidDataException("Invalid prepared window or worker identity.");
        if (ExitSettlement is { } settlement
            && (settlement.ObservedAt == default
                || !ProcessInstanceRecovery.IsConfirmedExited(settlement.Target,
                    checked((uint)request.ProcessId), checked((ulong)request.CreationFileTimeUtc))
                || !ProcessInstanceRecovery.IsConfirmedExited(settlement.Worker,
                    checked((uint)worker.ProcessId), checked((ulong)worker.CreationFileTimeUtc))))
            throw new InvalidDataException("Exit settlement requires both original process instances to have ended.");
        if (Result is not { } result) return;
        if (result.Prepared != Prepared || result.Cleanup is not { ProcessStarted: true } cleanup
            || cleanup.ExitObserved != cleanup.ExitCode.HasValue
            || (cleanup.TerminationError.HasValue && !cleanup.TerminationRequested)
            || (result.PersistencePending && result.AuthorizationMayHaveBeenSent)
            || (result.Completion is not null && (!result.AuthorizationMayHaveBeenSent || result.Rejection is not null)))
            throw new InvalidDataException("The result does not belong to this prepared single execution.");
        if (result.Rejection is { } rejection && !Enum.IsDefined(rejection.Reason))
            throw new InvalidDataException("Unknown window rejection.");
        if (result.Completion is { } completed) GpuWindowActionProtocol.ValidateCompletion(completed, request.Method);
        if (!Enum.IsDefined(result.Outcome) || result.Outcome != GpuWindowActionResult.ResolveOutcome(
            Prepared, result.Completion, result.Rejection, result.AuthorizationMayHaveBeenSent, cleanup, result.Failure))
            throw new InvalidDataException("The stored outcome contradicts the actual execution facts.");
    }
}

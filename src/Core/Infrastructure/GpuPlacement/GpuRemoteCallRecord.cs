using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal sealed record GpuRemoteCallExitSettlement(
    DateTimeOffset ObservedAt, RecoveryReadResult<ProcessInstanceRecoverySnapshot> Target);

internal sealed record GpuRemoteCallRecord(
    GpuRemoteCallRequest Request, GpuRemoteCallSnapshot? Started = null,
    GpuRemoteCallSnapshot? Result = null, GpuRemoteCallSnapshot? Settlement = null,
    GpuRemoteCallExitSettlement? ExitSettlement = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false
    };

    internal bool BlocksProcess => ExitSettlement is null
        && (Result is null || (Settlement ?? Result).ResourcesReleased == false);
    internal string RecordId => $"gpu-remote-call:{Request.CallId:D}";

    internal HostManagerAppliedRecord Encode()
    {
        Validate();
        return new(HostManagerAppliedRecordKinds.GpuRemoteCall, RecordId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["remoteCallVersion"] = "1", ["remoteCallPayload"] = JsonSerializer.Serialize(this, Json)
            });
    }

    internal static bool IsActionFact(HostManagerAppliedRecord record)
        => record.Kind.Equals(HostManagerAppliedRecordKinds.GpuRemoteCall, StringComparison.OrdinalIgnoreCase);

    internal static bool TryRead(HostManagerAppliedRecord record, [NotNullWhen(true)] out GpuRemoteCallRecord? fact)
    {
        fact = null;
        if (!IsActionFact(record) || record.Metadata?.GetValueOrDefault("remoteCallVersion") != "1"
            || record.Metadata.GetValueOrDefault("remoteCallPayload") is not { } payload) return false;
        try
        {
            var read = JsonSerializer.Deserialize<GpuRemoteCallRecord>(payload, Json);
            if (read is null || record.RecordId != read.RecordId) return false;
            read.Validate();
            fact = read;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { return false; }
    }

    private void Validate()
    {
        if (Request?.Process is not { } target || Request.CallId == Guid.Empty || target.ProcessId <= 4
            || target.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(target.ExecutablePath)
            || !Path.IsPathFullyQualified(target.ExecutablePath) || !Enum.IsDefined(Request.Kind)
            || Request.FunctionAddress == 0 || Request.ParameterByteLength <= 0)
            throw new InvalidDataException("A remote call requires its original precise process and request.");
        foreach (var snapshot in new[] { Started, Result, Settlement })
        {
            if (snapshot is null) continue;
            if (string.IsNullOrWhiteSpace(snapshot.Status) || snapshot.ThreadId == 0 || snapshot.ThreadCreationFileTimeUtc == 0
                || (snapshot.ThreadCreationFileTimeUtc.HasValue && !snapshot.ThreadId.HasValue)
                || (snapshot.ExitCode.HasValue && !snapshot.ThreadId.HasValue)
                || (snapshot.ThreadId.HasValue && snapshot.ParameterAddress == 0)
                || (snapshot.Response is { } response && (!Request.ReadResponse || response.Length != Request.ParameterByteLength))
                || (snapshot.Status == "completed" && !snapshot.ExitCode.HasValue))
                throw new InvalidDataException("Remote-call facts contradict the native request or result.");
            if (Started is { ThreadId: not null } original
                && (snapshot.ThreadId != original.ThreadId || snapshot.ThreadCreationFileTimeUtc != original.ThreadCreationFileTimeUtc
                    || snapshot.ParameterAddress != original.ParameterAddress))
                throw new InvalidDataException("A settlement cannot replace the original thread or allocation.");
        }
        if (Settlement is not null && (Result is null || Result.ResourcesReleased || !Settlement.ResourcesReleased))
            throw new InvalidDataException("A late settlement must finish the original unresolved call.");
        if (ExitSettlement is { } exit && (exit.ObservedAt == default
            || !ProcessInstanceRecovery.IsConfirmedExited(exit.Target, checked((uint)target.ProcessId), target.ProcessStartKey)))
            throw new InvalidDataException("The original target must be confirmed exited.");
    }
}

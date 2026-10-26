using System.Text.Json.Serialization;

namespace ResourceManager.App.Application.Optimization;

// A holder is the open subscription request, cancelled on disconnect or key revocation.
public sealed record RuntimeCpuLeaseTarget(string? SoftwareId = null, int? ProcessId = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    ulong? ProcessStartKey = null, string? ExecutablePath = null);

public sealed record RuntimeCpuLeaseRequest(RuntimeCpuLeaseTarget Target,
    IReadOnlyList<string> PhysicalCoreIds, bool Lock, bool Exclusive);

public sealed record RuntimeCpuLeaseEffect(int ProcessId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    ulong ProcessStartKey, string SoftwareId,
    string Role, string State, string? ReasonCode, int NativeErrorCode,
    IReadOnlyList<uint>? ObservedCpuSetIds, long? ObservedAffinityMask,
    IReadOnlyList<string>? ObservedPhysicalCoreIds, bool ChangedByManager);

public sealed record RuntimeCpuLeaseSnapshot(string LeaseId, RuntimeCpuLeaseRequest Request,
    string Holder, DateTimeOffset CreatedAt, string State, string? ReasonCode,
    DateTimeOffset? ObservedAt, IReadOnlyList<RuntimeCpuLeaseEffect> Effects);

public interface IRuntimeCpuLeaseService
{
    RuntimeCpuLeaseSnapshot CreateCpuLease(RuntimeCpuLeaseRequest request, string holder);
    Task<RuntimeCpuLeaseSnapshot?> ReadCpuLeaseAsync(string leaseId, CancellationToken cancellationToken);
    bool DeleteCpuLease(string leaseId);
    Task WaitForCpuLeaseEndAsync(string leaseId, CancellationToken cancellationToken);
}

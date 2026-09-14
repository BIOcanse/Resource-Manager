using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerPlacementNativeIdentity(
    ulong TargetKey,
    ulong RecordKey);

internal sealed record HostManagerPlacementProjectedRecord(
    HostManagerPlacementNativeIdentity Identity,
    HostManagerAppliedPlacementReceipt Placement,
    HostManagerAppliedRecord Record,
    int RowIndex,
    NativePlacementAppliedInput Input);

internal sealed record HostManagerPlacementDesired(
    HostManagerAppliedPlacementReceipt Placement,
    HostManagerAppliedRecord Record,
    uint Priority)
{
    internal RunningGpuPlacementActionPlan? RuntimeGpuAction { get; init; }
    internal HostManagerCycleEffectReservation? ActionReservation { get; init; }
}

internal sealed record HostManagerPlacementProjectedDesired(
    HostManagerPlacementNativeIdentity Identity,
    HostManagerAppliedPlacementReceipt Placement,
    HostManagerAppliedRecord Record,
    uint Priority,
    NativePlacementDesiredInput Input)
{
    internal RunningGpuPlacementActionPlan? RuntimeGpuAction { get; init; }
    internal HostManagerCycleEffectReservation? ActionReservation { get; init; }
}

internal sealed class HostManagerPlacementProjection
{
    private readonly IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementProjectedRecord> records;

    internal HostManagerPlacementProjection(
        uint rowCount,
        IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementProjectedRecord> records)
    {
        RowCount = rowCount;
        this.records = records;
    }

    internal uint RowCount { get; }

    internal IReadOnlyCollection<HostManagerPlacementProjectedRecord> Records => records.Values.ToArray();

    internal HostManagerPlacementProjectedRecord Require(NativePlacementAction action)
    {
        var identity = new HostManagerPlacementNativeIdentity(action.TargetKey, action.RecordKey);
        return records.TryGetValue(identity, out var record)
            ? record
            : throw new InvalidDataException(
                $"Native placement action {action.ActionId} does not map to an exact durable receipt.");
    }
}

internal sealed class HostManagerPlacementDesiredProjection
{
    private readonly IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementProjectedDesired> records;

    internal HostManagerPlacementDesiredProjection(
        uint rowCount,
        IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementProjectedDesired> records)
    {
        RowCount = rowCount;
        this.records = records;
    }

    internal uint RowCount { get; }

    internal IReadOnlyCollection<HostManagerPlacementProjectedDesired> Records => records.Values.ToArray();

    internal HostManagerPlacementProjectedDesired Require(NativePlacementAction action)
    {
        var identity = new HostManagerPlacementNativeIdentity(action.TargetKey, action.RecordKey);
        return records.TryGetValue(identity, out var record)
            ? record
            : throw new InvalidDataException(
                $"Native placement action {action.ActionId} does not map to an exact desired record.");
    }

    internal bool TryGet(
        HostManagerPlacementNativeIdentity identity,
        out HostManagerPlacementProjectedDesired record)
        => records.TryGetValue(identity, out record!);
}

internal static class HostManagerPlacementCoordinatorProjection
{
    internal static HostManagerPlacementDesiredProjection ProjectDesired(
        IReadOnlyList<HostManagerPlacementDesired> desired,
        Span<NativePlacementDesiredInput> destination)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Count > destination.Length)
        {
            throw new InvalidDataException(
                $"Placement desired count {desired.Count} exceeds the explicit native capacity {destination.Length}.");
        }

        destination.Clear();
        var records = new Dictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementProjectedDesired>(
            desired.Count);
        for (var rowIndex = 0; rowIndex < desired.Count; rowIndex++)
        {
            var row = desired[rowIndex];
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(row.Placement);
            ArgumentNullException.ThrowIfNull(row.Record);
            if (GpuActionFacts.IsActionFact(row.Record))
                throw new InvalidDataException("A recorded GPU action cannot be a desired placement.");
            var identity = CreateIdentity(row.Placement, row.Record);
            var input = CreateDesiredInput(identity, row);
            if (!records.TryAdd(
                identity,
                new HostManagerPlacementProjectedDesired(
                    identity,
                    row.Placement,
                    row.Record,
                    row.Priority,
                    input)
                {
                    RuntimeGpuAction = row.RuntimeGpuAction,
                    ActionReservation = row.ActionReservation
                }))
            {
                throw new InvalidDataException(
                    $"Duplicate or colliding placement desired identity {identity.TargetKey:X16}/{identity.RecordKey:X16}.");
            }
            destination[rowIndex] = input;
        }

        return new HostManagerPlacementDesiredProjection(checked((uint)desired.Count), records);
    }

    internal static HostManagerPlacementProjection ProjectApplied(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        Span<NativePlacementAppliedInput> destination)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var requiredCount = 0;
        foreach (var placement in placements)
        {
            ArgumentNullException.ThrowIfNull(placement);
            requiredCount = checked(requiredCount + placement.Records.Count(static record => !GpuActionFacts.IsActionFact(record)));
        }
        if (requiredCount > destination.Length)
        {
            throw new InvalidDataException(
                $"Placement receipt count {requiredCount} exceeds the explicit native capacity {destination.Length}.");
        }

        destination.Clear();
        var records = new Dictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementProjectedRecord>(requiredCount);
        var rowIndex = 0;
        foreach (var placement in placements)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(placement.TargetId);
            foreach (var record in placement.Records)
            {
                ArgumentNullException.ThrowIfNull(record);
                if (GpuActionFacts.IsActionFact(record)) continue;
                ArgumentException.ThrowIfNullOrWhiteSpace(record.Kind);
                ArgumentException.ThrowIfNullOrWhiteSpace(record.RecordId);

                var identity = CreateIdentity(placement, record);
                var input = CreateAppliedInput(identity, placement, record);
                if (!records.TryAdd(
                    identity,
                    new HostManagerPlacementProjectedRecord(identity, placement, record, rowIndex, input)))
                {
                    throw new InvalidDataException(
                        $"Duplicate or colliding placement receipt identity {identity.TargetKey:X16}/{identity.RecordKey:X16}.");
                }

                destination[rowIndex++] = input;
            }
        }

        return new HostManagerPlacementProjection(checked((uint)rowIndex), records);
    }

    private static NativePlacementDesiredInput CreateDesiredInput(
        HostManagerPlacementNativeIdentity identity,
        HostManagerPlacementDesired desired)
    {
        if (!TryResolveKinds(desired.Record, out var resourceKind, out var placementKind))
        {
            throw new InvalidDataException(
                $"Placement desired record kind {desired.Record.Kind} is unsupported.");
        }

        var validMask = NativePlacementDesiredValidity.Required;
        uint processId = 0;
        ulong processStartKey = 0;
        if (TryReadProcessIdentity(desired.Record, out processId, out processStartKey))
        {
            validMask |= NativePlacementDesiredValidity.ProcessIdentity;
        }

        return new NativePlacementDesiredInput
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementDesiredInput>(),
            Flags = (uint)NativePlacementDesiredFlags.None,
            TargetKey = identity.TargetKey,
            RecordKey = identity.RecordKey,
            ResourceKind = (uint)resourceKind,
            PlacementKind = (uint)placementKind,
            DesiredDigest = CreatePayloadDigest("applied", desired.Placement, desired.Record),
            ProcessStartKey = processStartKey,
            ProcessId = processId,
            Priority = desired.Priority,
            ValidMask = (ulong)validMask
        };
    }

    private static NativePlacementAppliedInput CreateAppliedInput(
        HostManagerPlacementNativeIdentity identity,
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record)
    {
        if (!TryResolveKinds(record, out var resourceKind, out var placementKind))
        {
            return new NativePlacementAppliedInput
            {
                StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementAppliedInput>(),
                Flags = (uint)NativePlacementAppliedFlags.None,
                TargetKey = identity.TargetKey,
                RecordKey = identity.RecordKey,
                ResourceKind = (uint)NativePlacementResourceKind.Unknown,
                PlacementKind = (uint)NativePlacementKind.Unknown,
                ObservationStatus = (uint)NativePlacementObservationStatus.Unchecked,
                ValidMask = (ulong)NativePlacementAppliedValidity.Identity
            };
        }

        var receiptDigest = CreatePayloadDigest("applied", placement, record);
        var previousDigest = CreatePayloadDigest("previous", placement, record);
        if (receiptDigest == previousDigest)
        {
            throw new InvalidDataException("Placement applied and previous payload digests collided.");
        }

        var validMask = NativePlacementAppliedValidity.Required;
        uint processId = 0;
        ulong processStartKey = 0;
        if (TryReadProcessIdentity(record, out processId, out processStartKey))
        {
            validMask |= NativePlacementAppliedValidity.ProcessIdentity;
        }

        return new NativePlacementAppliedInput
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementAppliedInput>(),
            Flags = (uint)NativePlacementAppliedFlags.PayloadValid,
            TargetKey = identity.TargetKey,
            RecordKey = identity.RecordKey,
            ResourceKind = (uint)resourceKind,
            PlacementKind = (uint)placementKind,
            ReceiptDigest = receiptDigest,
            PreviousDigest = previousDigest,
            CurrentDigest = 0,
            ProcessStartKey = processStartKey,
            ProcessId = processId,
            ObservationStatus = (uint)NativePlacementObservationStatus.Unchecked,
            ValidMask = (ulong)validMask
        };
    }

    internal static HostManagerPlacementNativeIdentity CreateIdentity(
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record)
        => new(
            NativeStableIdentity.CreateCaseInsensitiveKey(
                string.Concat(placement.ResourceKind, "\0", placement.TargetId)),
            NativeStableIdentity.CreateCaseInsensitiveKey(
                string.Concat(record.Kind, "\0", record.RecordId)));

    private static bool TryResolveKinds(
        HostManagerAppliedRecord record,
        out NativePlacementResourceKind resourceKind,
        out NativePlacementKind placementKind)
    {
        (resourceKind, placementKind) = record.Kind switch
        {
            HostManagerAppliedRecordKinds.CpuThreadCpuSets =>
                (NativePlacementResourceKind.Cpu, NativePlacementKind.CpuSets),
            HostManagerAppliedRecordKinds.CpuAffinity when
                record.Metadata?.GetValueOrDefault("affinityKind")
                    ?.Equals("cpu-sets", StringComparison.OrdinalIgnoreCase) == true =>
                (NativePlacementResourceKind.Cpu, NativePlacementKind.CpuSets),
            HostManagerAppliedRecordKinds.CpuAffinity =>
                (NativePlacementResourceKind.Cpu, NativePlacementKind.CpuAffinity),
            HostManagerAppliedRecordKinds.ProcessPriority =>
                (NativePlacementResourceKind.Cpu, NativePlacementKind.ProcessPriority),
            HostManagerAppliedRecordKinds.ProcessMemoryPriority =>
                (NativePlacementResourceKind.Cpu, NativePlacementKind.ProcessMemoryPriority),
            HostManagerAppliedRecordKinds.GpuPreference =>
                (NativePlacementResourceKind.Gpu, NativePlacementKind.GpuPreference),
            HostManagerAppliedRecordKinds.GpuShimPolicy when GpuShimPolicyRecord.TryRead(record, out _) =>
                (NativePlacementResourceKind.Gpu, NativePlacementKind.GpuShimPolicy),
            HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger =>
                (NativePlacementResourceKind.Gpu, NativePlacementKind.GpuRuntimeRebuild),
            _ => (NativePlacementResourceKind.Unknown, NativePlacementKind.Unknown)
        };
        return resourceKind != NativePlacementResourceKind.Unknown;
    }

    private static bool TryReadProcessIdentity(
        HostManagerAppliedRecord record,
        out uint processId,
        out ulong processStartKey)
    {
        processId = 0;
        processStartKey = 0;
        if (record.Metadata is null
            || !record.Metadata.TryGetValue("processId", out var processIdValue)
            || !uint.TryParse(processIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            || processId == 0
            || !TryReadProcessStartKey(record.Metadata, out processStartKey))
        {
            processId = 0;
            processStartKey = 0;
            return false;
        }
        return true;
    }

    private static bool TryReadProcessStartKey(
        IReadOnlyDictionary<string, string> metadata,
        out ulong processStartKey)
    {
        processStartKey = 0;
        if (metadata.TryGetValue("processStartKey", out var exactValue))
        {
            return ulong.TryParse(
                    exactValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out processStartKey)
                && processStartKey != 0;
        }
        if (!metadata.TryGetValue("processStartedAt", out var legacyValue)
            || !long.TryParse(
                legacyValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixMilliseconds)
            || unixMilliseconds <= 0)
        {
            return false;
        }

        try
        {
            processStartKey = checked((ulong)DateTimeOffset
                .FromUnixTimeMilliseconds(unixMilliseconds)
                .ToFileTime());
            return processStartKey != 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            processStartKey = 0;
            return false;
        }
        catch (OverflowException)
        {
            processStartKey = 0;
            return false;
        }
    }

    internal static ulong CreatePayloadDigest(
        string phase,
        HostManagerAppliedPlacementReceipt placement,
        HostManagerAppliedRecord record)
    {
        var canonical = new StringBuilder();
        var policyValue = record.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy;
        if (!policyValue) AppendField(canonical, phase);
        AppendField(canonical, placement.ResourceKind);
        AppendField(canonical, placement.TargetId);
        AppendField(canonical, record.Kind);
        AppendField(canonical, record.RecordId);
        if (policyValue)
        {
            if (!GpuShimPolicyRecord.TryRead(record, out var policy))
                throw new InvalidDataException("GPU shim policy receipt is invalid.");
            var value = phase switch
            {
                "applied" => policy.AppliedValue,
                "previous" => policy.PreviousValue,
                _ => throw new ArgumentOutOfRangeException(nameof(phase))
            };
            AppendField(canonical, value is null ? "absent" : "present");
            AppendField(canonical, value is null ? string.Empty : Convert.ToBase64String(value));
        }
        else
        {
            var metadata = record.Metadata ?? new Dictionary<string, string>();
            canonical.Append(metadata.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
            foreach (var pair in metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                AppendField(canonical, pair.Key);
                AppendField(canonical, pair.Value);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        for (var offset = 0; offset < hash.Length; offset += sizeof(ulong))
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(offset, sizeof(ulong)));
            if (value != 0)
            {
                return value;
            }
        }
        throw new InvalidDataException("Placement payload digest produced no nonzero lane.");
    }

    private static void AppendField(StringBuilder builder, string value)
        => builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
}

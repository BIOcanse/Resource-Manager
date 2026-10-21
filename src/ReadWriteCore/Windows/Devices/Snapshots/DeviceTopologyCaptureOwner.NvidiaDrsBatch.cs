using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    // All sessions for this due item produce one operation-local merge and one original Current publication.
    internal async ValueTask<FieldCollectionResult> CaptureNvidiaDrsBatchAsync(
        ImmutableArray<NvidiaDrsInformationRequest> requests, NvidiaDrsReadSource source,
        DeviceTopologyCapturePermission permission, CancellationToken token,
        Action<IReadOnlySet<string>>? collectionCommitted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(permission);
        token.ThrowIfCancellationRequested();
        if (requests.IsDefaultOrEmpty) return new([], false);
        await sampleGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            var original = ReadState();
            var pending = original.InformationFields;
            var queried = ImmutableArray.CreateBuilder<FieldReference>();
            foreach (var request in requests)
            {
                token.ThrowIfCancellationRequested();
                if (!permission.IsActive() || !permission.HasCurrentDemand()) return new([], false);
                if (request.Fields.IsEmpty) continue;
                DeviceInformationSnapshot? read = null;
                try
                {
                    queried.AddRange(request.Fields);
                    var typed = source(request.Profiles, request.Limits, token);
                    if (typed is not null) read = WindowsInformationFieldProjection.ProjectNvidiaDrs(request, typed, DateTimeOffset.UtcNow, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { read = null; }
                token.ThrowIfCancellationRequested();
                pending = read is null ? WindowsInformationFieldProjection.NvidiaDrsUnavailable(pending, request, token)
                    : WindowsInformationFieldProjection.MergeNvidiaDrs(pending, request, read, token);
            }
            token.ThrowIfCancellationRequested();
            if (queried.Count == 0) return new([], false);
            var fields = queried.Distinct().ToImmutableArray();
            var committed = false;
            var accepted = TryPublish(permission, () =>
            {
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref state, original with { StateRevision = Interlocked.Increment(ref stateRevision), InformationFields = pending });
                InformationPublications.Publish([InformationCollectionItemId]); committed = true;
                collectionCommitted?.Invoke(fields.Select(field => field.DeviceIdentity).ToHashSet(StringComparer.Ordinal));
            });
            var selections = fields.Select(field => new InformationFieldSelection(field,
                DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue)).ToImmutableArray();
            return new(InformationReadWriteBackend.Read(selections, ReadState().InformationFields), accepted && committed, fields);
        }
        finally { sampleGate.Release(); }
    }
}

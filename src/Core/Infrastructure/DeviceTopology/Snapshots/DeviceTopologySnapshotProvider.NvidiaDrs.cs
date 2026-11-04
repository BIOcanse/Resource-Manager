using ResourceManager.Subscription;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    private NvidiaNvapiMonitoringZone NvidiaDrsZone => nvidiaDrsZone
        ?? throw new InvalidOperationException("The original NVAPI monitoring zone is not bound.");

    internal NvidiaDrsInformationAccess CreateNvidiaDrsInformation(NvidiaDrsInformationRequest scope)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return captureOwner.CreateNvidiaDrsInformation(scope, NvidiaDrsZone.ReadDrs, DrsPermission(null));
    }

    internal async ValueTask<ImmutableArray<FieldReading>> ReadNvidiaDrsInformationAsync(
        NvidiaDrsInformationRequest scope, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var value = await SubscriptionSingleFetch.ReadAsync(SubscribeNvidiaDrsFieldsAsync(
            "single-fetch:nvidia-drs", scope, SubscriptionSingleFetch.Interval,
            new(FieldSubscriptionSamplingMode.FreshOnTrigger), token), static _ => true, token);
        return value.IsDefault ? [] : value;
    }

    internal async ValueTask<DeviceInformationSnapshot?> DiscoverNvidiaDrsInformationAsync(NvidiaDrsReadLimits limits, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lease = samplingSubscriptionOwner.Acquire(2);
        return await captureOwner.DiscoverNvidiaDrsDirectoryAsync(limits, NvidiaDrsZone.DiscoverDrs, token,
            DrsPermission(new(() => lease.OwnerToken.IsActive, lease.OwnerToken.TryPublish, () => true)));
    }

    private DeviceTopologyCapturePermission DrsPermission(DeviceTopologyCapturePermission? outer)
        => new(() =>
        {
            lock (publicationGate)
                return Volatile.Read(ref disposed) == 0 && (outer?.IsActive() ?? true);
        }, publish =>
        {
            // Direct survives Stop, but a queued direct request may not publish after provider Dispose.
            // All actual Host captures enter publication -> owner token, never the reverse.
            lock (publicationGate)
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                var committed = false;
                void Commit() { publish(); committed = true; }
                var accepted = outer is null;
                if (outer is null) Commit(); else accepted = outer.TryPublish(Commit);
                return accepted && committed;
            }
        }, () => outer?.HasCurrentDemand() ?? true);

    internal async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeNvidiaDrsFieldsAsync(string id,
        NvidiaDrsInformationRequest scope, TimeSpan interval, FieldSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken token)
    {
        var access = CreateNvidiaDrsInformation(scope);
        await foreach (var value in SubscribeFieldsAsync(id, scope.Fields, interval, options, access.Backend,
            DeviceTopologyFieldScope.NvidiaDrs, token, scope)) yield return value;
    }

    private async Task<DeviceTopologySampleCompletion> TrySampleNvidiaDrsAsync(SamplingOwnerToken ownerToken,
        CancellationToken token, ImmutableArray<NvidiaDrsInformationRequest> scopes,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted)
    {
        if (!ownerToken.IsActive || token.IsCancellationRequested) return DeviceTopologySampleCompletion.Canceled;
        if (!HasCurrentDemand(DeviceTopologySampleScope.NvidiaDrs)) return DeviceTopologySampleCompletion.Sampled;
        try
        {
            var requests = NvidiaDrsInformationBatch.Create(scopes.IsDefault ? [] : scopes);
            var result = await captureOwner.CaptureNvidiaDrsBatchAsync(requests, NvidiaDrsZone.ReadDrs,
                DrsPermission(Permission(ownerToken, DeviceTopologySampleScope.NvidiaDrs, hosted: true)), token,
                keys => collectionCommitted?.Invoke(DeviceTopologySampleScope.NvidiaDrs, keys));
            if (!result.Committed) return DeviceTopologySampleCompletion.Canceled;
            return DeviceTopologySampleCompletion.Sampled;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return DeviceTopologySampleCompletion.Canceled; }
    }
}

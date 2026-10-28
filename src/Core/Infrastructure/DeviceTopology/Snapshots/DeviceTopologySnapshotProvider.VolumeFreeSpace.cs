using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    internal const string VolumeFreeSpaceItemId = DeviceTopologyCaptureOwner.VolumeFreeSpaceItemId;
    private readonly WindowsVolumeSpaceInformationReader volumeSpaceReader = volumeSpace ?? new();
    private static readonly BusinessBlockReason VolumeFreeSpaceChanged = new(BusinessBlockRequestKind.Dependency,
        nameof(DeviceTopologySnapshotProvider), VolumeFreeSpaceItemId, 0);

    internal DeviceInformationSnapshot ReadVolumeFreeSpaceInformation() => captureOwner.ReadVolumeFreeSpace();

    internal ValueTask<DeviceInformationSnapshot> CaptureAllVolumeFreeSpaceInformationAsync(CancellationToken token)
        => CaptureVolumeFreeSpaceInformationAsync(ReadVolumeFreeSpaceInformation().Devices
            .Select(static device => new FieldReference(device.DeviceIdentity, DeviceInformationFieldRegistry.VolumeAllFreeBytesActual.Name))
            .ToImmutableArray(), token);

    internal async ValueTask<DeviceInformationSnapshot> CaptureVolumeFreeSpaceInformationAsync(
        ImmutableArray<FieldReference> fields, CancellationToken token)
    {
        using var itemUse = SubscribeVolumeFreeSpaceItem($"direct-volume:{Interlocked.Increment(ref nextLeaseId)}");
        using var lease = samplingSubscriptionOwner.Acquire(2);
        var completion = await captureOwner.SampleVolumeFreeSpaceAsync(volumeSpaceReader, fields.Select(static field => field.DeviceIdentity)
            .Distinct(StringComparer.Ordinal).ToArray(), Permission(lease.OwnerToken, DeviceTopologySampleScope.VolumeFreeSpace),
            token, requireDemand: false);
        token.ThrowIfCancellationRequested();
        if (completion == DeviceTopologySampleCompletion.Canceled) throw new OperationCanceledException(token);
        return ReadVolumeFreeSpaceInformation();
    }

    private async Task<DeviceTopologySampleCompletion> TrySampleVolumeFreeSpaceAsync(SamplingOwnerToken ownerToken,
        ImmutableArray<FieldReference> fields, CancellationToken token,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? committed)
    {
        var queried = false;
        var result = await captureOwner.SampleVolumeFreeSpaceAsync(volumeSpaceReader,
            fields.Select(static field => field.DeviceIdentity).Distinct(StringComparer.Ordinal).ToArray(),
            Permission(ownerToken, DeviceTopologySampleScope.VolumeFreeSpace, hosted: true,
                committed: () => committed?.Invoke(DeviceTopologySampleScope.VolumeFreeSpace,
                    queried ? new HashSet<string>(StringComparer.Ordinal) { VolumeFreeSpaceItemId } : new HashSet<string>(StringComparer.Ordinal))),
            token, sourceQueryStarted: () => queried = true);
        return result;
    }

    private static Func<DeviceInformationSnapshot, Task>? CreateVolumeFreeSpacePublisher(StateAuthority? authority)
    {
        if (authority is null) return null; // Manually composed owners have no state directory.
        var cell = authority.ClaimCell<DeviceInformationSnapshot>(StateItemUids.DeviceTopologyVolumeFreeSpaceCurrent);
        return next =>
        {
            var previous = authority.Read<DeviceInformationSnapshot>(StateItemUids.DeviceTopologyVolumeFreeSpaceCurrent);
            return !previous.HasValue || !DeviceInformationContent.Equals(previous.Value!, next)
                ? cell.PublishAsync(next, VolumeFreeSpaceChanged) : Task.CompletedTask;
        };
    }

    private IDisposable? SubscribeVolumeFreeSpaceItem(string holder)
        => stateAuthority?.Subscribe(StateItemUids.DeviceTopologyVolumeFreeSpaceCurrent, holder, static _ => Task.CompletedTask);
}

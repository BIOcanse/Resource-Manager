using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    public const string VolumeFreeSpaceItemId = "deviceTopology.volume-free-space";
    private Task volumePublication = Task.CompletedTask;

    private void PublishVolumeFreeSpaceCurrent()
    {
        if (volumeFreeSpacePublished is not null)
            volumePublication = volumeFreeSpacePublished(ReadVolumeFreeSpace());
    }

    private async Task CompleteVolumeFreeSpacePublicationAsync()
    {
        var pending = volumePublication;
        volumePublication = Task.CompletedTask;
        await pending.ConfigureAwait(false);
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public DeviceInformationSnapshot ReadVolumeFreeSpace()
    {
        var fields = ReadState().InformationFields;
        return new(fields.ObservedAt, fields.Devices.Select(device => device with {
            Fields = device.Fields.Where(static field => field.Definition == DeviceInformationFieldRegistry.VolumeAllFreeBytesActual).ToArray()
        }).Where(static device => device.Fields.Count > 0).ToArray());
    }

    public async Task<DeviceTopologySampleCompletion> SampleVolumeFreeSpaceAsync(WindowsVolumeSpaceInformationReader reader,
        IReadOnlyList<string> identities, DeviceTopologyCapturePermission? permission, CancellationToken token,
        bool requireDemand = true, Action? sourceQueryStarted = null)
    {
        if (requireDemand && !await sampleGate.WaitAsync(0, token)) return DeviceTopologySampleCompletion.SkippedBusy;
        if (!requireDemand) await sampleGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (permission is not null && !permission.IsActive()) return DeviceTopologySampleCompletion.Canceled;
            if (requireDemand && permission is not null && !permission.HasCurrentDemand()) return DeviceTopologySampleCompletion.Sampled;
            token.ThrowIfCancellationRequested();
            sourceQueryStarted?.Invoke();
            var rows = reader.ReadSelected(identities);
            var observedAt = DateTimeOffset.UtcNow;
            var values = WindowsInformationFieldProjection.Volumes(rows, observedAt);
            token.ThrowIfCancellationRequested();
            if (!TryPublish(permission, () =>
            {
                var current = ReadState();
                var information = current.InformationFields;
                var next = new DeviceInformationSnapshot(information.ObservedAt, information.Devices.Select(device => device with {
                    Fields = device.Fields.Select(field => field.Definition == DeviceInformationFieldRegistry.VolumeAllFreeBytesActual
                        && values.FindField(new(device.DeviceIdentity, field.Definition.Name)) is { } value
                        ? value with { ObservedAt = observedAt } : field).ToArray()
                }).ToArray());
                Volatile.Write(ref state, current with { InformationFields = next });
                PublishVolumeFreeSpaceCurrent();
                InformationPublications.Publish([VolumeFreeSpaceItemId]);
                publicationSignal.Publish();
            })) return DeviceTopologySampleCompletion.Canceled;
            return DeviceTopologySampleCompletion.Sampled;
        }
        finally
        {
            try { await CompleteVolumeFreeSpacePublicationAsync(); }
            finally { sampleGate.Release(); }
        }
    }
}

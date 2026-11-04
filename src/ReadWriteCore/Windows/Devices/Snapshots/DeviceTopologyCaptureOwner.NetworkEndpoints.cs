using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    private DeviceInformationSnapshot networkEndpoints = DeviceInformationSnapshot.Empty;
    private readonly CurrentValuePublicationSignal networkEndpointPublications = new();

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public DeviceInformationSnapshot ReadNetworkEndpoints() => Volatile.Read(ref networkEndpoints);
    public CurrentValuePublicationSignal.Subscription SubscribeNetworkEndpointPublications() => networkEndpointPublications.Subscribe();

    public async Task<DeviceTopologySampleCompletion> SampleNetworkEndpointsAsync(
        WindowsNetworkEndpointInformationReader reader, DeviceTopologyCapturePermission? permission,
        CancellationToken cancellationToken, bool requireDemand = true, Action? sourceQueryStarted = null, Action<Exception>? logFailure = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (requireDemand && !await sampleGate.WaitAsync(0, cancellationToken)) return DeviceTopologySampleCompletion.SkippedBusy;
        if (!requireDemand) await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (permission is not null && !permission.IsActive()) return DeviceTopologySampleCompletion.Canceled;
            if (requireDemand && permission is not null && !permission.HasCurrentDemand()) return DeviceTopologySampleCompletion.Sampled;
            sourceQueryStarted?.Invoke();
            DeviceInformationSnapshot next;
            var failed = false;
            try { next = reader.ReadFields(); }
            catch (Exception error)
            {
                logFailure?.Invoke(error);
                next = WindowsInformationFieldProjection.NetworkEndpoints(
                    ResourceManager.ReadWrite.Windows.Devices.Models.DeviceTopologyNetworkEndpointInformation.Unavailable, null);
                failed = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryPublish(permission, () =>
            {
                Volatile.Write(ref networkEndpoints, next);
                networkEndpointPublications.Publish();
            })) return DeviceTopologySampleCompletion.Canceled;
            return failed ? DeviceTopologySampleCompletion.Failed : DeviceTopologySampleCompletion.Sampled;
        }
        finally { sampleGate.Release(); }
    }
}
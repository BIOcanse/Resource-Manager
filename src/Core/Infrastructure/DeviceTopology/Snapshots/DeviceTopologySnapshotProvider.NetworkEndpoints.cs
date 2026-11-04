using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using System.Runtime.CompilerServices;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    internal DeviceInformationSnapshot ReadNetworkEndpointInformation() => captureOwner.ReadNetworkEndpoints();

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeNetworkEndpointsAsync(string id,
        TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken, bool includeCurrent = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = captureOwner.SubscribeNetworkEndpointPublications();
        using var lease = TrackInformationDemand(new DeviceTopologySampleRequest(
            $"lease:{Interlocked.Increment(ref nextLeaseId)}:{id}", DeviceTopologySampleScope.NetworkEndpoints,
            cancellationToken), interval);
        if (includeCurrent) yield return captureOwner.ReadNetworkEndpoints();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return captureOwner.ReadNetworkEndpoints();
        }
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureNetworkEndpointInformationAsync(CancellationToken token)
    {
        using var lease = samplingSubscriptionOwner.Acquire(2);
        await captureOwner.SampleNetworkEndpointsAsync(networkEndpointReader,
            Permission(lease.OwnerToken, DeviceTopologySampleScope.NetworkEndpoints), token, requireDemand: false, logFailure: LogNetworkFailure);
        return captureOwner.ReadNetworkEndpoints();
    }

    private Task<DeviceTopologySampleCompletion> TrySampleNetworkEndpointsAsync(SamplingOwnerToken ownerToken,
        CancellationToken token, Action<DeviceTopologySampleScope, IReadOnlySet<string>>? committed)
    {
        var queried = false;
        return captureOwner.SampleNetworkEndpointsAsync(networkEndpointReader,
            Permission(ownerToken, DeviceTopologySampleScope.NetworkEndpoints, hosted: true,
                committed: () => committed?.Invoke(DeviceTopologySampleScope.NetworkEndpoints,
                    queried ? new HashSet<string>(StringComparer.Ordinal) { NetworkEndpointsItemId } : new HashSet<string>(StringComparer.Ordinal))),
            token, sourceQueryStarted: () => queried = true, logFailure: LogNetworkFailure);
    }
    private void LogNetworkFailure(Exception error) => logger.LogWarning(error, "Network endpoint capture failed; current connection fields were cleared.");
}
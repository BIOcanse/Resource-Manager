using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider
{
    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeAcpiAsync(
        string subscriptionId, TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = captureOwner.SubscribeAcpiPublications();
        using var lease = AcquireInformationSubscription(subscriptionId, interval, acpi: true, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return ReadState().AcpiInformationFields;
        }
    }

    internal Task<DeviceTopologySampleCompletion> SampleAcpiNowAsync(CancellationToken cancellationToken)
        => captureOwner.SampleAcpiAsync(null, cancellationToken);

}

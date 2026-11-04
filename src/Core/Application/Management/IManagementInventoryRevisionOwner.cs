using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Management;

public sealed class ManagementInventoryComponentObservationToken
{
    internal ManagementInventoryComponentObservationToken(ulong componentRevision, object changeFence)
    {
        ComponentRevision = componentRevision;
        ChangeFence = changeFence;
    }

    internal ulong ComponentRevision { get; }

    internal object ChangeFence { get; }
}

public interface IManagementInventoryRevisionOwner
{
    ManagementInventoryRevisionSnapshot Current { get; }

    ManagementInventoryComponentObservationToken CaptureComponentObservationToken();

    void ObserveSoftwareContent(
        string? windowsSid,
        IReadOnlyList<SoftwareRecord> records);

    void ObserveSoftwareInventoryContentChanged();

    void ObserveComponentContentChanged();

    void ObserveComponentContentChanged(string componentId);

    void ObserveComponentContentChanged(string? windowsSid, ComponentStatus component);

    IDisposable BeginComponentChange(string componentId);

    void ObserveComponents(
        string? windowsSid,
        IReadOnlyList<ComponentStatus> components,
        ManagementInventoryComponentObservationToken? observedComponent = null);

    void ObserveComponent(
        string? windowsSid,
        ComponentStatus component,
        ManagementInventoryComponentObservationToken? observedComponent = null);

    void ObserveBrowserRuntimeSnapshot(BrowserRuntimeSnapshot snapshot);

    IAsyncEnumerable<ManagementInventoryRevisionSnapshot> SubscribeAsync(
        CancellationToken cancellationToken);
}

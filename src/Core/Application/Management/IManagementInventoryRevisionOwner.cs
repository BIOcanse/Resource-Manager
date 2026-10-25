using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Management;

public interface IManagementInventoryRevisionOwner
{
    ManagementInventoryRevisionSnapshot Current { get; }

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
        string? observedComponentRevision = null);

    void ObserveComponent(
        string? windowsSid,
        ComponentStatus component,
        string? observedComponentRevision = null);

    void ObserveBrowserRuntimeSnapshot(BrowserRuntimeSnapshot snapshot);

    IAsyncEnumerable<ManagementInventoryRevisionSnapshot> SubscribeAsync(
        CancellationToken cancellationToken);
}

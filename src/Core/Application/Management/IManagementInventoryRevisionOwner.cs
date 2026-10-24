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

    void ObserveComponents(string? windowsSid, IReadOnlyList<ComponentStatus> components);

    void ObserveComponent(string? windowsSid, ComponentStatus component);

    void ObserveBrowserRuntimeSnapshot(BrowserRuntimeSnapshot snapshot);

    IAsyncEnumerable<ManagementInventoryRevisionSnapshot> SubscribeAsync(
        CancellationToken cancellationToken);
}

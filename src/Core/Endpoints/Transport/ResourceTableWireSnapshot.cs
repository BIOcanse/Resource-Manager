using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record ResourceTableWireSnapshot(
    DateTimeOffset? CapturedAt,
    IReadOnlyList<ResourceTableColumn> Columns,
    IReadOnlyList<ResourceTableRow> Rows,
    ResourceTableSort Sort,
    string ViewMode)
{
    internal static ResourceTableWireSnapshot Create(
        ResourceTableSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ResourceTableWireSnapshot(
            source.CapturedAt,
            source.Columns,
            source.Rows,
            source.Sort,
            source.ViewMode);
    }
}

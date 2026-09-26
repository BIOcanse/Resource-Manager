using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Application.ResourceTable;

public interface IResourceTableProjector
{
    ResourceTableSnapshot Project(
        ResourceBreakdownSnapshot breakdown,
        ResourceTableRequest request);
}

public interface IResourceTableProviderStateSource
{
    IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns);
}

using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Application.ResourceTable;

public interface IResourceTableProviderStateSource
{
    IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns);
}

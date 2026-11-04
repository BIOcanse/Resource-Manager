using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Application.Operations;

public interface IHostManagerOperationQueryService
{
    HostManagerOperationSnapshot? Get(string operationId);

    HostManagerOperationsPublishedState GetPublishedState();

    IAsyncEnumerable<HostManagerOperationsPublishedState> SubscribeAsync(
        CancellationToken cancellationToken);

    IReadOnlyList<HostManagerOperationSnapshot> GetRecent();

    HostManagerOperationCoordinatorHealth GetHealth();
}

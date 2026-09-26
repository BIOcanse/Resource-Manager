using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Application.Operations;

public interface IHostManagerOperationCommandService
{
    Task<HostManagerOperationSnapshot> SubmitAsync(
        HostManagerOperationSubmitCommand command,
        CancellationToken cancellationToken);

    Task<HostManagerOperationSnapshot?> CancelAsync(
        string operationId,
        CancellationToken cancellationToken);
}

using ResourceManager.App.Domain.TrustedAccess;

namespace ResourceManager.App.Application.TrustedAccess;

public sealed record TrustedAccessRequestCreateCommand(
    string Name,
    IReadOnlyList<string> RequestedScopes,
    string ObservedExecutablePath,
    int ObservedProcessId);

public interface ITrustedAccessRequestOwner
{
    ValueTask<TrustedAccessRequestCreateResult> CreateAsync(
        TrustedAccessRequestCreateCommand command,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<TrustedAccessRequestPendingSnapshot>> SubscribePendingAsync(
        CancellationToken cancellationToken);

    ValueTask<TrustedAccessRequestWaitResult> WaitAsync(
        string requestId,
        string deliverySecret,
        CancellationToken cancellationToken);

    ValueTask<TrustedAccessRequestDecisionResult> ApproveAsync(
        string requestId,
        IReadOnlyList<string> scopes,
        IReadOnlyCollection<string> approverScopes,
        bool approverIsAdministrator,
        CancellationToken cancellationToken);

    ValueTask<TrustedAccessRequestDecisionResult> DenyAsync(
        string requestId,
        TrustedAccessRequestDenialReason reason,
        CancellationToken cancellationToken);
}

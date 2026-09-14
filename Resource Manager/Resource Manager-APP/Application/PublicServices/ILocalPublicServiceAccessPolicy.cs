using ResourceManager.App.Domain.PublicServices;

namespace ResourceManager.App.Application.PublicServices;

public interface ILocalPublicServiceAccessPolicy
{
    Task<LocalPublicServiceAccessDecision> EvaluateAsync(
        HttpContext context,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        ulong completionHandle,
        CancellationToken cancellationToken);
}

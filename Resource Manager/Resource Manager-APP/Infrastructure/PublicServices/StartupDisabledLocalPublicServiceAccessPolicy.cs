using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Domain.PublicServices;

namespace ResourceManager.App.Infrastructure.PublicServices;

internal sealed class StartupDisabledLocalPublicServiceAccessPolicy
    : ILocalPublicServiceAccessPolicy
{
    private static readonly LocalPublicServiceAccessDecision Denied = new(
        Allowed: false,
        StatusCode: StatusCodes.Status404NotFound,
        Reason: "service-disabled",
        CompletionHandle: 0);

    public Task<LocalPublicServiceAccessDecision> EvaluateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Denied);
    }

    public ValueTask CompleteAsync(
        ulong completionHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completionHandle != 0)
        {
            throw new InvalidOperationException(
                "The disabled public-service policy cannot own a request completion.");
        }

        return ValueTask.CompletedTask;
    }
}

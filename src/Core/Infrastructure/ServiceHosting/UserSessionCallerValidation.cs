using System.Security.Principal;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

internal static class UserSessionCallerValidation
{
    internal static void RequireActiveUser(
        SoftwareOperationCaller? caller,
        IInteractiveUserSessionBroker broker)
    {
        if (caller is null || caller.WindowsSessionId == 0
            || !broker.GetActiveSessionIds().Contains(caller.WindowsSessionId)
            || broker.GetUserSid(caller.WindowsSessionId) != caller.WindowsSid)
            throw new UnauthorizedAccessException("The requesting user session is no longer available.");

        _ = new SecurityIdentifier(caller.WindowsSid);
    }
}

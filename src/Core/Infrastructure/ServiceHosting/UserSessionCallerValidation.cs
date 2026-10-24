using System.Security.Principal;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

internal static class UserSessionCallerValidation
{
    internal static void RequireLauncherSession(
        TrustedAdapterCallerIdentity? caller, uint expectedSession, string expectedSid,
        string expectedLauncher, IInteractiveUserSessionBroker broker)
    {
        // A UAC credential prompt can use another administrator's SID. Only the
        // attested process session selects the desktop; the caller SID never does.
        if (caller is null || expectedSession == 0 || caller.WindowsSessionId != expectedSession
            || !caller.IsElevated || caller.IntegrityLevelRid < 0x3000
            || !StringComparer.OrdinalIgnoreCase.Equals(caller.CanonicalExecutablePath, Path.GetFullPath(expectedLauncher))
            || !broker.GetActiveSessionIds().Contains(expectedSession)
            || broker.GetUserSid(expectedSession) != expectedSid)
            throw new UnauthorizedAccessException("The requesting launcher session is not authorized.");

        _ = new SecurityIdentifier(expectedSid);
    }

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

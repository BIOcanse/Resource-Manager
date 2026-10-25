using ResourceManager.App.Domain.Adaptation;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

internal static class SessionLaunchExecutor
{
    internal static uint Launch(IInteractiveUserSessionBroker sessions, TrustedAdapterCallerIdentity caller,
        bool verifiedAdministrator, SessionLaunchRequest request)
    {
        if (caller.WindowsSessionId <= 0
            || !sessions.GetActiveSessionIds().Contains(checked((uint)caller.WindowsSessionId)))
            throw new UnauthorizedAccessException("The requesting Windows session is unavailable.");
        var session = checked((uint)caller.WindowsSessionId);
        var desktopSid = sessions.GetUserSid(session);
        if (desktopSid is null || caller.WindowsSid != desktopSid
            && !(verifiedAdministrator && caller.IsElevated && caller.IntegrityLevelRid >= 0x3000))
            throw new UnauthorizedAccessException("The requesting identity cannot use this desktop session.");
        // The broker rechecks the WTS token against this SID immediately before CreateProcessAsUser.
        return sessions.LaunchDesktopProcess(session, desktopSid, request.ExecutablePath, request.Arguments);
    }
}

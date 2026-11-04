namespace ResourceManager.App.Infrastructure.ServiceHosting;

public sealed class InteractiveUserSessionActivationTracker
{
    private readonly HashSet<uint> activeSessions = [];

    public void MarkLaunchFailed(uint sessionId) => activeSessions.Remove(sessionId);

    public IReadOnlyList<uint> Observe(IEnumerable<uint> activeSessionIds)
    {
        ArgumentNullException.ThrowIfNull(activeSessionIds);

        var observed = new HashSet<uint>();
        foreach (var sessionId in activeSessionIds)
        {
            if (sessionId != 0)
            {
                observed.Add(sessionId);
            }
        }

        activeSessions.RemoveWhere(sessionId => !observed.Contains(sessionId));

        var newlyActive = new List<uint>();
        foreach (var sessionId in observed.Order())
        {
            if (activeSessions.Add(sessionId))
            {
                newlyActive.Add(sessionId);
            }
        }

        return newlyActive;
    }
}

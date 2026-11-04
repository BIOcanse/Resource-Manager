namespace ResourceManager.App.Infrastructure.ServiceHosting;

public interface IInteractiveUserSessionBroker
{
    IReadOnlyList<uint> GetActiveSessionIds();

    string? GetUserSid(uint sessionId) => null;

    uint LaunchDesktopProcess(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        => throw new NotSupportedException("Desktop-token process launching is unavailable.");

    uint LaunchUserShell(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        => throw new NotSupportedException("User shell launching is unavailable.");

    Task<int> LaunchUserShellAndWaitAsync(
        uint sessionId,
        string expectedUserSid,
        string executablePath,
        string arguments,
        CancellationToken cancellationToken)
        => Task.FromException<int>(new NotSupportedException("User shell completion is unavailable."));
}

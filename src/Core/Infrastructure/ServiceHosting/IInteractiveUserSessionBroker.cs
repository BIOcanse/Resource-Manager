namespace ResourceManager.App.Infrastructure.ServiceHosting;

public enum NativeUiProcessPresence
{
    Absent,
    Expected,
    Conflicting
}

public interface IInteractiveUserSessionBroker
{
    IReadOnlyList<uint> GetActiveSessionIds();

    string? GetUserSid(uint sessionId) => null;

    NativeUiProcessPresence GetNativeUiProcessPresence(
        uint sessionId,
        string expectedExecutablePath);

    uint LaunchNativeUi(
        uint sessionId,
        string executablePath,
        string arguments);

    uint LaunchNativeUi(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        => throw new NotSupportedException("Identity-bound Native UI launching is unavailable.");

    uint LaunchUserShell(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        => throw new NotSupportedException("User shell launching is unavailable.");
}

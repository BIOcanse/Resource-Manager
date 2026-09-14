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

    NativeUiProcessPresence GetNativeUiProcessPresence(
        uint sessionId,
        string expectedExecutablePath);

    uint LaunchNativeUi(
        uint sessionId,
        string executablePath,
        string arguments);
}

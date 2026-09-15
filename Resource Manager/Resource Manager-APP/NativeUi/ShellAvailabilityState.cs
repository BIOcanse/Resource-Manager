using ResourceManager.NativeUi.Localization;

namespace ResourceManager.NativeUi;

internal enum FrontendConnectionState
{
    NotStarted,
    Loading,
    Ready,
    Unavailable
}

internal sealed class FrontendConnectionStateChangedEventArgs(
    FrontendConnectionState state,
    string? detail = null) : EventArgs
{
    public FrontendConnectionState State { get; } = state;

    public string? Detail { get; } = detail;
}

internal sealed record ShellAvailabilitySnapshot(
    string StatusText,
    string RetryActionText,
    bool RetryEnabled);

internal static class ShellAvailabilityProjection
{
    public static ShellAvailabilitySnapshot Project(
        BackendConnectionState backend,
        FrontendConnectionState frontend)
    {
        var text = NativeUiText.Current;
        return backend switch
    {
        BackendConnectionState.Connecting => new(
            text.StatusConnecting,
            text.ActionReconnectBackend,
            RetryEnabled: false),
        BackendConnectionState.Reconnecting => new(
            text.StatusReconnecting,
            text.ActionReconnectBackend,
            RetryEnabled: false),
        BackendConnectionState.Degraded => new(
            text.StatusBackendUnavailable,
            text.ActionReconnectBackend,
            RetryEnabled: true),
        BackendConnectionState.ShuttingDown => new(
            text.StatusShuttingDown,
            text.ActionReconnectBackend,
            RetryEnabled: false),
        BackendConnectionState.Ready => frontend switch
        {
            FrontendConnectionState.NotStarted => new(
                text.StatusBackendReady,
                text.ActionReloadFrontend,
                RetryEnabled: false),
            FrontendConnectionState.Loading => new(
                text.StatusFrontendLoading,
                text.ActionReloadFrontend,
                RetryEnabled: false),
            FrontendConnectionState.Ready => new(
                text.StatusReady,
                text.ActionReloadFrontend,
                RetryEnabled: false),
            FrontendConnectionState.Unavailable => new(
                text.StatusFrontendUnavailable,
                text.ActionReloadFrontend,
                RetryEnabled: true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(frontend),
                frontend,
                null)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };
    }
}

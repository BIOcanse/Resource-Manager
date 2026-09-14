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
        FrontendConnectionState frontend) => backend switch
    {
        BackendConnectionState.Connecting => new(
            "正在连接本地服务",
            "重新连接本地服务",
            RetryEnabled: false),
        BackendConnectionState.Reconnecting => new(
            "正在重新连接本地服务",
            "重新连接本地服务",
            RetryEnabled: false),
        BackendConnectionState.Degraded => new(
            "本地服务不可用",
            "重新连接本地服务",
            RetryEnabled: true),
        BackendConnectionState.ShuttingDown => new(
            "正在退出",
            "重新连接本地服务",
            RetryEnabled: false),
        BackendConnectionState.Ready => frontend switch
        {
            FrontendConnectionState.NotStarted => new(
                "本地服务已就绪",
                "重新加载界面",
                RetryEnabled: false),
            FrontendConnectionState.Loading => new(
                "正在加载界面",
                "重新加载界面",
                RetryEnabled: false),
            FrontendConnectionState.Ready => new(
                "已就绪",
                "重新加载界面",
                RetryEnabled: false),
            FrontendConnectionState.Unavailable => new(
                "界面不可用",
                "重新加载界面",
                RetryEnabled: true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(frontend),
                frontend,
                null)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };
}

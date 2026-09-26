using ResourceManager.NativeUi;

namespace ResourceManager.App.Tests;

public sealed class ShellAvailabilityProjectionTests
{
    [Fact]
    public void ReadyBackend_ProjectsTheIndependentFrontendState()
    {
        var cases = new[]
        {
            (FrontendConnectionState.NotStarted, "本地服务已就绪", false),
            (FrontendConnectionState.Loading, "正在加载界面", false),
            (FrontendConnectionState.Ready, "已就绪", false),
            (FrontendConnectionState.Unavailable, "界面不可用", true)
        };

        foreach (var (frontend, expectedStatus, expectedRetryEnabled) in cases)
        {
            var result = ShellAvailabilityProjection.Project(
                BackendConnectionState.Ready,
                frontend);

            Assert.Equal(expectedStatus, result.StatusText);
            Assert.Equal("重新加载界面", result.RetryActionText);
            Assert.Equal(expectedRetryEnabled, result.RetryEnabled);
        }
    }

    [Fact]
    public void NonReadyBackend_WinsOverAnyFrontendState()
    {
        var cases = new[]
        {
            (BackendConnectionState.Connecting, "正在连接本地服务", false),
            (BackendConnectionState.Reconnecting, "正在重新连接本地服务", false),
            (BackendConnectionState.Degraded, "本地服务不可用", true),
            (BackendConnectionState.ShuttingDown, "正在退出", false)
        };

        foreach (var (backend, expectedStatus, expectedRetryEnabled) in cases)
        {
            var result = ShellAvailabilityProjection.Project(
                backend,
                FrontendConnectionState.Ready);

            Assert.Equal(expectedStatus, result.StatusText);
            Assert.Equal("重新连接本地服务", result.RetryActionText);
            Assert.Equal(expectedRetryEnabled, result.RetryEnabled);
        }
    }
}

using System.Diagnostics;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class InteractiveUserSessionBrokerCompletionTests
{
    [Fact]
    public async Task DuplicatedProcessHandleReturnsExitCodeAfterOriginalHandleIsDisposed()
    {
        using var process = StartCommand("/d /c exit 37");
        var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
            process.SafeHandle.DangerousGetHandle());
        process.Dispose();

        Assert.Equal(37, await WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
            waitHandle,
            CancellationToken.None,
            timeoutMilliseconds: 10_000));
    }

    [Fact]
    public async Task CancellationClosesWaitHandleWithoutTerminatingLaunchedProcess()
    {
        using var process = StartCommand("/d /c timeout /t 2 /nobreak >nul");
        var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
            process.SafeHandle.DangerousGetHandle());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
                    waitHandle,
                    cancellation.Token));

            Assert.False(process.HasExited);
        }
        finally
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TimeoutIsReportedAndClosesWaitHandleWithoutTerminatingProcess()
    {
        using var process = StartCommand("/d /c timeout /t 2 /nobreak >nul");
        var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
            process.SafeHandle.DangerousGetHandle());

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
                    waitHandle,
                    CancellationToken.None,
                    timeoutMilliseconds: 50));

            Assert.False(process.HasExited);
        }
        finally
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Process StartCommand(string arguments)
    {
        var processPath = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        return Process.Start(new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Test command did not start.");
    }
}

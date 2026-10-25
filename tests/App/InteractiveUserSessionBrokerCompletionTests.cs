using System.Diagnostics;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class InteractiveUserSessionBrokerCompletionTests
{
    [Fact]
    public async Task DuplicatedProcessHandleReturnsExitCodeAfterOriginalHandleIsDisposed()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/d /c exit 37",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The synthetic child process did not start.");
        var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
            process.SafeHandle.DangerousGetHandle());
        process.Dispose();

        Assert.Equal(37, await WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
            waitHandle,
            CancellationToken.None,
            waitSliceMilliseconds: 10_000));
    }

    [Fact]
    public async Task CancellationRetainsWaitHandleAndOwnershipUntilActualChildExit()
    {
        using var gate = SyntheticOwnedProcessGate.Create(37);
        using var process = Process.Start(gate.StartInfo)
            ?? throw new InvalidOperationException("The synthetic child process did not start.");
        var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
            process.SafeHandle.DangerousGetHandle());
        using var cancellation = new CancellationTokenSource();
        var cancellationObserved = 0;
        var wait = WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
            waitHandle,
            cancellation.Token,
            waitSliceMilliseconds: 50,
            onCancellationObserved: () =>
            {
                Interlocked.Increment(ref cancellationObserved);
                throw new IOException("synthetic diagnostic observer failure");
            });

        try
        {
            await Task.Delay(100);
            cancellation.Cancel();
            await Task.Delay(100);

            Assert.Equal(1, Volatile.Read(ref cancellationObserved));
            Assert.False(wait.IsCompleted);
            Assert.False(process.HasExited);
            Assert.False(waitHandle.IsClosed);
            gate.Release();
            Assert.Equal(37, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(waitHandle.IsClosed);
        }
        finally
        {
            gate.Release();
            if (!wait.IsCompleted)
                await wait.WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ElapsedWaitSlicesAreObservableAndDoNotReleaseOwnedProcess()
    {
        using var gate = SyntheticOwnedProcessGate.Create(23);
        using var process = Process.Start(gate.StartInfo)
            ?? throw new InvalidOperationException("The synthetic child process did not start.");
        var waitHandle = WindowsInteractiveUserSessionBroker.DuplicateProcessHandleForWaiting(
            process.SafeHandle.DangerousGetHandle());
        var elapsedSlices = 0;
        var wait = WindowsInteractiveUserSessionBroker.WaitForProcessExitAsync(
            waitHandle,
            CancellationToken.None,
            waitSliceMilliseconds: 50,
            onWaitSliceElapsed: () =>
            {
                if (Interlocked.Increment(ref elapsedSlices) == 1)
                    throw new IOException("synthetic diagnostic observer failure");
            });

        try
        {
            await Task.Delay(150);
            Assert.True(Volatile.Read(ref elapsedSlices) >= 1);
            Assert.False(wait.IsCompleted);
            Assert.False(process.HasExited);
            Assert.False(waitHandle.IsClosed);
            gate.Release();
            Assert.Equal(23, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(waitHandle.IsClosed);
        }
        finally
        {
            gate.Release();
            if (!wait.IsCompleted)
                await wait.WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

}

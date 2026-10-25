using System.Diagnostics;
using System.Text.Json;
using ResourceManager.Shared.Runtime.Sessions;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

public sealed class UserShellProcessLauncherTests
{
    [Fact]
    public void WaitForExitFlagRoundTripsAndOlderRequestsDefaultToNonWaiting()
    {
        var request = new UserShellExecuteRequest("setup.exe", "/quiet", "runas", WaitForExit: true);

        Assert.Equal(request, UserShellExecuteRequest.Decode(request.Encode()));

        var olderRequest = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            FileName = "setup.exe",
            Arguments = "/quiet",
            Verb = "runas"
        }));
        Assert.False(UserShellExecuteRequest.Decode(olderRequest).WaitForExit);
    }

    [Fact]
    public void NonWaitingShellStartPreservesSuccessfulNullProcessBehavior()
    {
        Assert.Equal(0, UserShellProcessLauncher.ExecuteShellStart(static () => null));
    }

    [Fact]
    public void WaitingShellStartTreatsNullProcessAsFailure()
    {
        Assert.Equal(2, UserShellProcessLauncher.ExecuteShellStart(static () => null, waitForExit: true));
    }

    [Fact]
    public void WaitingShellStartReturnsTheChildExitCode()
    {
        var processPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var result = UserShellProcessLauncher.ExecuteShellStart(
            () => Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = "/d /c exit 23",
                CreateNoWindow = true,
                UseShellExecute = false
            }),
            waitForExit: true);

        Assert.Equal(23, result);
    }

    [Fact]
    public async Task ShellWrapperRepeatsBoundedWaitSlicesUntilOwnedChildExit()
    {
        using var gate = SyntheticOwnedProcessGate.Create(29);
        var elapsedSlices = 0;
        var firstSlice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Task.Run(() => UserShellProcessLauncher.ExecuteShellStart(
            () => Process.Start(gate.StartInfo),
            waitForExit: true,
            waitSliceMilliseconds: 50,
            onWaitSliceElapsed: () =>
            {
                if (Interlocked.Increment(ref elapsedSlices) == 1)
                {
                    firstSlice.TrySetResult();
                    throw new IOException("synthetic diagnostic observer failure");
                }
            }));

        try
        {
            await firstSlice.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Volatile.Read(ref elapsedSlices) >= 1);
            Assert.False(execution.IsCompleted);
            gate.Release();
            Assert.Equal(29, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            gate.Release();
            if (!execution.IsCompleted)
                await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using ResourceManager.NativeUi;
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
}

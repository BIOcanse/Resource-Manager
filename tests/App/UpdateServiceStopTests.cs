using System.ComponentModel;
using ResourceManager.Shared.ServiceHosting;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class UpdateServiceStopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyAStillRunningOwnedInstanceNeedsTermination(bool scmTimedOut)
    {
        var calls = new List<string>();
        var terminated = false;
        WindowsUpdateRuntime.StopAndWait(() =>
        {
            calls.Add("stop");
            if (scmTimedOut) throw new TimeoutException("SCM stop timeout");
        }, timeout =>
        {
            calls.Add("wait:" + timeout);
            return terminated;
        }, () => { calls.Add("terminate-owned"); terminated = true; }, () => calls.Add("stopped"));
        Assert.Equal(new[] { "stop", "wait:" + (scmTimedOut ? 0 : 30000), "terminate-owned", "wait:10000", "stopped" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyExitedInstanceIsNeverTerminatedEvenAfterScmTimeout(bool scmTimedOut)
    {
        var stopped = false;
        WindowsUpdateRuntime.StopAndWait(() => { if (scmTimedOut) throw new TimeoutException(); },
            _ => true, () => throw new Xunit.Sdk.XunitException("Unexpected termination"), () => stopped = true);
        Assert.True(stopped);
    }

    [Fact]
    public void PermissionFailureNeverBecomesPermissionToTerminate()
    {
        var error = new Win32Exception(5);
        Assert.Same(error, Assert.Throws<Win32Exception>(() => WindowsUpdateRuntime.StopAndWait(
            () => throw error, _ => throw new Xunit.Sdk.XunitException("Unexpected wait"),
            () => throw new Xunit.Sdk.XunitException("Unexpected termination"),
            () => throw new Xunit.Sdk.XunitException("Unexpected confirmation"))));
    }

    [Fact]
    public void UnprovenServiceIdentityPreservesOriginalScmTimeout()
    {
        var error = new TimeoutException("original SCM timeout");
        Assert.Same(error, Assert.Throws<TimeoutException>(() => WindowsUpdateRuntime.StopAndWait(
            () => throw error, _ => throw new Xunit.Sdk.XunitException("Unexpected wait"), null,
            () => throw new Xunit.Sdk.XunitException("Unexpected confirmation"))));
    }

    [Fact]
    public void StoppedScmDoesNotPermitSwitchWhileAnUnownedBackendStillLives()
        => Assert.Throws<TimeoutException>(() => WindowsUpdateRuntime.StopAndWait(() => { }, _ => false,
            null, () => throw new Xunit.Sdk.XunitException("Unexpected confirmation")));

    [Theory]
    [InlineData("kill-failed")]
    [InlineData("still-alive")]
    [InlineData("scm-not-stopped")]
    public void IncompleteTerminationOrConfirmationFailsBeforeSwitch(string failure)
    {
        var terminated = false;
        Assert.ThrowsAny<Exception>(() => WindowsUpdateRuntime.StopAndWait(() => { },
            _ => terminated && failure != "still-alive", () =>
            {
                if (failure == "kill-failed") throw new Win32Exception(5);
                terminated = true;
            }, () => throw new TimeoutException("SCM not stopped")));
    }

    [Theory]
    [InlineData(4u, 100u, 100, true)]
    [InlineData(4u, 101u, 100, false)]
    [InlineData(4u, 0u, 100, false)]
    [InlineData(1u, 100u, 100, false)]
    [InlineData(3u, 100u, 100, false)]
    public void ForceStopRequiresTheOriginalRunningScmPid(uint state, uint scmPid, int capturedPid, bool expected)
    {
        var path = Path.GetFullPath("owned/ResourceManager.exe");
        var service = new WindowsServiceState(state, 3, "\"" + path + "\"", "LocalSystem") { ProcessId = scmPid };
        Assert.Equal(expected, WindowsUpdateRuntime.IsOwnedServiceProcess(service, capturedPid, path, path));
        Assert.False(WindowsUpdateRuntime.IsOwnedServiceProcess(service, capturedPid, path + ".other", path));
    }

    [Theory]
    [InlineData("different-account")]
    [InlineData("different-image")]
    public void AChangedServiceRegistrationCannotAuthorizeTermination(string failure)
    {
        var path = Path.GetFullPath("owned/ResourceManager.exe");
        var service = new WindowsServiceState(4, 3,
            failure == "different-image" ? path + ".other" : path,
            failure == "different-account" ? "LocalService" : "LocalSystem") { ProcessId = 100 };
        Assert.Throws<InvalidOperationException>(() => WindowsUpdateRuntime.IsOwnedServiceProcess(service, 100, path, path));
    }
}

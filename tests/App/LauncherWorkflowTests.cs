using System.ComponentModel;
using ResourceManager.Launcher;
using ResourceManager.Shared.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class LauncherWorkflowTests
{
    private static readonly string Backend = Path.GetFullPath("owned-backend.exe");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaunchesInterfaceOnlyAfterVerifiedSystemService(bool elevated)
    {
        var state = new WindowsServiceState(1, 3, Backend, "LocalSystem");
        var steps = new List<string>();
        LauncherWorkflow.Start(elevated, false, Backend, () => state,
            () => { steps.Add("direct"); state = state with { State = 4 }; },
            () => { steps.Add("elevated"); state = state with { State = 4 }; },
            () => { Assert.True(state.Running); steps.Add("direct-ui"); },
            requestInterface: () => { Assert.True(state.Running); steps.Add("session-ui"); });
        Assert.Equal([elevated ? "direct" : "elevated", elevated ? "session-ui" : "direct-ui"], steps);
    }

    [Fact]
    public void AlreadyElevatedStartCannotFallBackToAnElevatedUi()
    {
        var launched = false;
        Assert.Throws<InvalidOperationException>(() => LauncherWorkflow.Start(true, false, Backend,
            () => new(4, 3, Backend, "LocalSystem"), () => { }, () => { }, () => launched = true));
        Assert.False(launched);
    }

    [Fact]
    public void ServiceOpeningFailureKeepsItsReasonWithoutDirectUiFallback()
    {
        var launched = false;
        var failure = Assert.Throws<Win32Exception>(() => LauncherWorkflow.Start(true, false, Backend,
            () => new(4, 3, Backend, "LocalSystem"), () => { }, () => { }, () => launched = true,
            requestInterface: () => throw new Win32Exception(5)));
        Assert.Equal(5, failure.NativeErrorCode);
        Assert.False(launched);
    }

    [Theory]
    [InlineData(1223)]
    [InlineData(5)]
    [InlineData(1058)]
    public void FailureKeepsItsReasonAndDoesNotLaunchInterface(int error)
    {
        var state = new WindowsServiceState(1, 3, Backend, "LocalSystem");
        var started = false;
        var failure = Assert.Throws<Win32Exception>(() => LauncherWorkflow.Start(false, false, Backend, () => state,
            () => throw new Exception("unexpected direct start"), () => throw new Win32Exception(error), () => started = true));
        Assert.Equal(error, failure.NativeErrorCode);
        Assert.False(started);
    }

    [Theory]
    [InlineData("Administrator", 4u)]
    [InlineData("LocalSystem", 1u)]
    public void AdministratorBackendOrUnconfirmedStartCannotLaunchInterface(string account, uint status)
    {
        var started = false;
        Assert.Throws<InvalidOperationException>(() => LauncherWorkflow.Start(true, false, Backend,
            () => new(status, 3, Backend, account), () => { }, () => { }, () => started = true));
        Assert.False(started);
    }
}

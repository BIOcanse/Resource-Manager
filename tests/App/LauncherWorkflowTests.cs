using System.ComponentModel;
using ResourceManager.Launcher;
using ResourceManager.Shared.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed class LauncherWorkflowTests
{
    [Theory]
    [InlineData(false, "--show-existing")]
    [InlineData(true, "--background-startup")]
    public void InstalledUiEntryStartsTheResidentWithTheManifestEnforced(bool background, string flag)
    {
        var root = Path.GetFullPath("fixture installation");
        var layout = ResourceManager.Shared.Packages.ReleasePackageLayout.ForRoot(root, "fixture");
        var installation = new DesktopInstallation(root, layout.Backend, layout.NativeUi, layout.InternalLauncher);
        var start = installation.ResidentStartInfo(background);
        Assert.Equal(layout.Resident, start.FileName);
        Assert.NotEqual(layout.NativeUi, start.FileName);
        Assert.Equal(flag, start.Arguments);
        Assert.True(start.UseShellExecute);
        Assert.Equal(Path.GetDirectoryName(layout.Resident), start.WorkingDirectory);
    }
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
            () => { Assert.True(state.Running); steps.Add("ui"); });
        // One interface launch for both: the interface requires elevation, so an elevated launcher starts it
        // with its own token and an unelevated one gets the UAC consent prompt from ShellExecute.
        Assert.Equal([elevated ? "direct" : "elevated", "ui"], steps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunningServiceLaunchesTheInterfaceWithoutAnyServiceAction(bool elevated)
    {
        var steps = new List<string>();
        LauncherWorkflow.Start(elevated, false, Backend, () => new(4, 3, Backend, "LocalSystem"),
            () => steps.Add("direct"), () => steps.Add("elevated"), () => steps.Add("ui"));
        Assert.Equal(["ui"], steps);
    }

    [Theory]
    [InlineData(1223)]
    [InlineData(740)]
    public void InterfaceLaunchFailureKeepsItsReason(int error)
    {
        var failure = Assert.Throws<Win32Exception>(() => LauncherWorkflow.Start(true, false, Backend,
            () => new(4, 3, Backend, "LocalSystem"), () => { }, () => { }, () => throw new Win32Exception(error)));
        Assert.Equal(error, failure.NativeErrorCode);
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

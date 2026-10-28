using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using ResourceManager.ReadWrite.Helpers;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeRoundThreeTests
{
    [Fact]
    public async Task MissingReadingsSectionLeavesAvailabilityUnchangedAndNextRunCanStart()
    {
        var launches = 0;
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, startFactory: start =>
            {
                if (++launches == 1) start.Environment["RM_BRIDGE_FIXTURE_MODE"] = "closed-readings-section";
                return Process.Start(start);
            });
        var changes = new List<bool>();
        owner.AvailabilityChanged += changes.Add;
        await Assert.ThrowsAsync<FileNotFoundException>(() => owner.RunAsync(default, () => Assert.Fail("Missing section reported running")));
        Assert.True(owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.None, owner.UnavailableKind);
        Assert.Empty(changes);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = owner.RunAsync(stop.Token, () => ready.SetResult());
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(2, launches); }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task FileNotFoundFromAcceptedCommandDoesNotMeanHelperIsNotInstalled()
    {
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        await Assert.ThrowsAsync<FileNotFoundException>(() => owner.RunAsync(default, () => { },
            _ => Task.FromException(new FileNotFoundException("unrelated-command-file"))));
        Assert.True(owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.None, owner.UnavailableKind);
    }

    [Theory]
    [InlineData("resolver-null")]
    [InlineData("resolver-throws")]
    [InlineData("create-file")]
    [InlineData("create-win32")]
    public async Task MissingExecutableAtResolveOrCreateIsStillRecognized(string failure)
    {
        var resolutions = 0;
        using var owner = new HardwareBridgeProcessOwner(() =>
        {
            if (++resolutions > 1)
            {
                if (failure == "resolver-null") return null;
                if (failure == "resolver-throws") throw new FileNotFoundException("fixture-executable-missing");
            }
            return HardwareBridgeSessionTests.Fixture;
        }, HardwareBridgeSessionTests.Options, startFactory: _ => throw (failure == "create-win32"
            ? (Exception)new Win32Exception(2, "fixture-executable-missing") : new FileNotFoundException("fixture-executable-missing")));
        await Assert.ThrowsAnyAsync<Exception>(() => owner.RunAsync(default, () => Assert.Fail("Missing executable reported running")));
        Assert.False(owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.NotInstalled, owner.UnavailableKind);
    }

    [Fact]
    public async Task DisposeBeforeUnconfirmedCleanupReleasesChildHandleAndLifetime()
    {
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Microsoft.Win32.SafeHandles.SafeProcessHandle? handle = null;
        owner.WaitForChildExitAsync = async (process, _) =>
        {
            handle = process.SafeHandle;
            cleanupEntered.TrySetResult();
            await finishCleanup.Task;
            throw new TimeoutException("fixture-exit-observation-timeout");
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = owner.RunAsync(default, () => ready.SetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var lifetime = (CancellationTokenSource)Field(owner, "runningStop")!;
        owner.Dispose();
        try { await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { finishCleanup.TrySetResult(); }
        await Assert.ThrowsAnyAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(handle);
        Assert.True(handle.IsClosed);
        Assert.Null(Field(owner, "pendingExit"));
        Assert.Null(Field(owner, "runningStop"));
        Assert.Throws<ObjectDisposedException>(() => lifetime.Token);
    }

    private static object? Field(HardwareBridgeProcessOwner owner, string name)
        => typeof(HardwareBridgeProcessOwner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
}
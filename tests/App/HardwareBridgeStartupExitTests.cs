using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Helpers;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeStartupExitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task NativeStartupCleanupRetainsCreatedHandleUntilExitIsConfirmed(int intervalMilliseconds)
    {
        SafeProcessHandle? retained = null;
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { AvailabilityCheckInterval = TimeSpan.FromMilliseconds(intervalMilliseconds) },
            startFactory: start =>
            {
                // The real native path creates its suspended Job-bound process;
                // stream setup then fails before a child can be returned.
                start.StandardInputEncoding = new FailingEncoding();
                using var unexpected = HardwareBridgeChildProcess.Start(start,
                    startupCleanupExitConfirmed: handle => { retained = handle; return false; });
                throw new InvalidOperationException("Fixture unexpectedly completed stream setup");
            });
        await Assert.ThrowsAnyAsync<TimeoutException>(() => owner.RunAsync(default, () => Assert.Fail("Failed startup reported running")));
        Assert.False(owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.ExitNotConfirmed, owner.UnavailableKind);
        Assert.NotNull(retained);
        Assert.False(retained.IsClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RunAsync(default, () => Assert.Fail("Unconfirmed startup child overlapped")));
        await Assert.ThrowsAnyAsync<TimeoutException>(async () => await owner.SuspendForInstallationAsync(default));
        if (intervalMilliseconds == 0)
        {
            await Task.Delay(150);
            Assert.False(owner.IsAvailable);
            Assert.False(retained.IsClosed);
        }
        else
        {
            await BusinessBlockTestSupport.Until(() => owner.IsAvailable);
            Assert.Equal(HardwareBridgeUnavailableKind.None, owner.UnavailableKind);
            Assert.True(retained.IsClosed);
            await using (await owner.SuspendForInstallationAsync(default)) { }
        }
    }

    private sealed class FailingEncoding : UTF8Encoding
    {
        public override Encoder GetEncoder() => throw new InvalidOperationException("fixture-stream-setup-failed");
    }
}

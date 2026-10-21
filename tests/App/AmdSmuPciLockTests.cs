using ResourceManager.ReadWrite.Vendors.Amd.Smu;

namespace Resource_Manager_APP.Tests;

public sealed class AmdSmuPciLockTests
{
    [Fact]
    public void ContendedLockDoesNotDispatchHardwareCommand()
    {
        using var mutex = new Mutex(false);
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            mutex.WaitOne();
            ready.Set();
            try { release.Wait(TimeSpan.FromSeconds(10)); }
            finally { mutex.ReleaseMutex(); }
        });
        owner.Start();
        var calls = 0;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<TimeoutException>(() => AmdSmuPawnIoSession.ExecuteWithPciLock(
                mutex, TimeSpan.Zero, () => ++calls));
            Assert.Equal(0, calls);
        }
        finally { release.Set(); Assert.True(owner.Join(TimeSpan.FromSeconds(5))); }
        Assert.Equal(1, AmdSmuPawnIoSession.ExecuteWithPciLock(mutex, TimeSpan.Zero, () => ++calls));
    }

    [Fact]
    public async Task ActionFailureIsNotRetriedAndReleasesTheLock()
    {
        using var mutex = new Mutex(false);
        var calls = 0;
        Assert.Throws<UnauthorizedAccessException>(() => AmdSmuPawnIoSession.ExecuteWithPciLock<int>(
            mutex, TimeSpan.Zero, () => { calls++; throw new UnauthorizedAccessException(); }));
        Assert.Equal(1, calls);
        Assert.Equal(2, await Task.Run(() => AmdSmuPawnIoSession.ExecuteWithPciLock(
            mutex, TimeSpan.FromSeconds(1), () => ++calls)));
    }
}

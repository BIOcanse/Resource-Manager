using System.Diagnostics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessOwnershipTests
{
    [Fact]
    public void ProcessCollectionDisposesEveryEntryAfterEarlyReturn()
    {
        var processes = new[] { new TrackedResource(1), new TrackedResource(2), new TrackedResource(3) };

        var result = ProcessCollectionOwner.Use(processes, items => items[0].Id);

        Assert.Equal(1, result);
        Assert.All(processes, item => Assert.Equal(1, item.DisposeCount));
    }

    [Fact]
    public void ProcessCollectionDisposesEveryEntryWhenReaderThrows()
    {
        var processes = new[] { new TrackedResource(1), new TrackedResource(2), new TrackedResource(3) };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ProcessCollectionOwner.Use<TrackedResource, int>(processes,
                _ => throw new InvalidOperationException("read failed")));

        Assert.Equal("read failed", error.Message);
        Assert.All(processes, item => Assert.Equal(1, item.DisposeCount));
    }

    [Fact]
    public void ProcessCollectionContinuesDisposalWhenOneResourceDisposeThrows()
    {
        var processes = new[]
        {
            new TrackedResource(1, throwOnDispose: true),
            new TrackedResource(2),
            new TrackedResource(3)
        };

        Assert.Throws<IOException>(() => ProcessCollectionOwner.Use(processes, _ => true));

        Assert.All(processes, item => Assert.Equal(1, item.DisposeCount));
    }

    [Fact]
    public void ProcessCaptureBatchDisposesTheWholeRealProcessArrayWhenCaptureThrows()
    {
        var processes = CreateCurrentProcessArray();
        var handles = processes.Select(static process => process.SafeHandle).ToArray();
        using var owner = CreateOwner(processes);

        Assert.Throws<InvalidOperationException>(() => owner.CaptureBatch<int>(
            false, new ProcessCpuDeltaTracker(), ThrowCapture, static _ => false));

        Assert.All(handles, static handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public void ProcessCaptureBatchDisposesTheWholeRealProcessArrayWhenPreparationThrows()
    {
        var processes = CreateCurrentProcessArray();
        var handles = processes.Select(static process => process.SafeHandle).ToArray();
        using var owner = CreateOwner(processes);

        Assert.Throws<InvalidOperationException>(() => owner.CaptureBatch<int>(
            false, new ProcessCpuDeltaTracker(), ThrowCapture, static _ => false,
            prepare: static (_, _) => throw new InvalidOperationException("prepare failed")));

        Assert.All(handles, static handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public void ModuleSnapshotDisposesEveryEnumeratedModuleWhenProjectionThrows()
    {
        var modules = new[] { new TrackedResource(1), new TrackedResource(2), new TrackedResource(3) };
        var reads = 0;

        Assert.Throws<InvalidOperationException>(() => ProcessModuleSnapshotReader.ReadAndDispose(
            modules, _ => ++reads == 1 ? throw new InvalidOperationException("module read failed") : 0));

        Assert.Equal(1, reads);
        Assert.All(modules, item => Assert.Equal(1, item.DisposeCount));
    }

    [Fact]
    public void ModuleSnapshotDisposesEveryModuleYieldedBeforeEnumerationThrows()
    {
        var modules = new[] { new TrackedResource(1), new TrackedResource(2) };

        Assert.Throws<InvalidOperationException>(() => ProcessModuleSnapshotReader.ReadAndDispose(
            YieldThenFail(modules), module => module.Id));

        Assert.All(modules, item => Assert.Equal(1, item.DisposeCount));
    }

    [Fact]
    public void ImagePathReaderBindsItsCallbackToTheProcessCreationIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var processId = process.Id;
        var creationFileTime = process.StartTime.ToFileTimeUtc();
        var invoked = false;

        var path = WindowsProcessImagePathReader.TryRead(process, (actualPid, actualCreation) =>
        {
            invoked = true;
            Assert.Equal(processId, actualPid);
            Assert.Equal(creationFileTime, actualCreation);
            return "recorded.exe";
        });

        Assert.True(invoked);
        Assert.Equal("recorded.exe", path);
    }

    [Fact]
    public void ImagePathReaderRejectsInvalidIdentityWithoutCallingTheReader()
    {
        var invoked = false;

        var path = WindowsProcessImagePathReader.TryRead(0, 1, (_, _) =>
        {
            invoked = true;
            return "unexpected.exe";
        });

        Assert.Null(path);
        Assert.False(invoked);
    }

    private static IEnumerable<TrackedResource> YieldThenFail(TrackedResource[] resources)
    {
        foreach (var resource in resources) yield return resource;
        throw new InvalidOperationException("module enumeration failed");
    }

    private static Process[] CreateCurrentProcessArray()
        => [Process.GetCurrentProcess(), Process.GetCurrentProcess()];

    private static WindowsProcessFieldOwner CreateOwner(Process[] processes)
    {
        var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        owner.Inventory = new FixedInventory(processes);
        return owner;
    }

    private static bool ThrowCapture(Process process, ProcessCreationRead creation, out int sample, out TimeSpan cpu,
        out ProcessInstanceKey? identity)
    {
        sample = 0;
        cpu = default;
        identity = null;
        throw new InvalidOperationException("capture failed");
    }

    private sealed class TrackedResource(int id, bool throwOnDispose = false) : IDisposable
    {
        internal int Id { get; } = id;
        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose) throw new IOException("dispose failed");
        }
    }

    private sealed class FixedInventory(Process[] processes) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => processes;
    }
}

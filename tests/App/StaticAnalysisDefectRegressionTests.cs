using System.Collections;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.NativeUi.Overlay;
using ResourceManager.ReadWrite.Windows.Results.Explicit;
using ResourceManager.Shared.Overlay;

namespace Resource_Manager_APP.Tests;

public sealed class StaticAnalysisDefectRegressionTests
{
    [Fact]
    public void OverlayRejectsSmallExistingMappingDespiteFullCapacityHeader()
    {
        var id = new OverlayTargetId(Environment.ProcessId, 0x1234567800000000UL + (ulong)Random.Shared.Next());
        using var mapping = MemoryMappedFile.CreateNew(
            PerformanceOverlaySharedMemory.SectionName(id.ProcessId, checked((long)id.ProcessStartKey)), 4096);
        using var view = mapping.CreateViewAccessor();
        var header = new PerformanceOverlayHeader
        {
            Magic = PerformanceOverlaySharedMemory.Magic,
            Version = PerformanceOverlaySharedMemory.Version,
            HeaderBytes = PerformanceOverlaySharedMemory.HeaderBytes,
            PixelCapacity = PerformanceOverlaySharedMemory.MaximumPixelBytes
        };
        view.Write(0, ref header);
        var error = Assert.Throws<InvalidDataException>(() =>
        {
            using var section = new InjectedOverlaySection(id, 0);
        });
        Assert.Contains("4096", error.Message);
        Assert.Contains("capacity", error.Message);
        // Rejection leaves the pre-existing producer's view intact.
        Assert.Equal(header.Magic, view.ReadUInt32(0));
    }

    [Fact]
    public void WorkspaceCapacityPublishesEveryUidByteAndDistinguishesScratchIdentities()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var first = owner.CreateScratch(64);
        using var second = owner.CreateScratch(128);
        var rows = owner.ReadWorkspaceCapacities();
        Assert.Equal(2, rows.Count);
        Span<byte> bytes = stackalloc byte[16];
        first.Uid.CopyTo(bytes);
        Assert.Equal(Convert.ToHexString(bytes), Assert.Single(rows, row => row.Bytes == 64).Uid);
        second.Uid.CopyTo(bytes);
        Assert.Equal(Convert.ToHexString(bytes), Assert.Single(rows, row => row.Bytes == 128).Uid);
        Assert.Equal(2, rows.Select(row => row.Uid).Distinct().Count());
    }

    [Fact]
    public async Task InvalidConsumerCounterDoesNotPreventIndependentRegistrationOrDrain()
    {
        BusinessBlockRegistration[] declarations = [new(120, _ => throw new NotSupportedException())];
        var diagnostics = new BusinessBlockDiagnosticsRegistry(declarations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var controller = new TestBlockScheduler(
            [new TestBlockRegistration(120, [], _ => throw new NotSupportedException())],
            BusinessBlockTestSupport.CreateAuthority());
        // Keep all registration scopes on their registering threads. The corrupted
        // prior baseline makes a diagnostic read fail without affecting independent registration.
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = Task.Run(() =>
        {
            using var registration = diagnostics.RegisterConsumerThread(120, "prior");
            var entries = (IDictionary)Field(diagnostics, "entries");
            var metrics = entries[120]!.GetType().GetProperty("Metrics")!.GetValue(entries[120])!;
            var threads = (IDictionary)Field(metrics, "activeThreads");
            var state = threads.Values.Cast<object>().Single();
            var baseline = state.GetType().GetProperty("LastCpuTicks")!;
            var saved = baseline.GetValue(state);
            baseline.SetValue(state, long.MaxValue);
            entered.SetResult(state);
            try { Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            finally { baseline.SetValue(state, saved); }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // This synchronous call cannot resume on the prior registration thread.
            using var accepted = diagnostics.RegisterConsumerThread(120, "independent");
            Assert.Throws<InvalidOperationException>(() => diagnostics.Read(120));
            Assert.Equal(BusinessBlockState.Stopped, Assert.Single(controller.Read().Blocks).State);
        }
        finally { release.Set(); await prior.WaitAsync(TimeSpan.FromSeconds(5)); }
        var facts = diagnostics.Read(120);
        Assert.Empty(facts.Metrics.ActiveThreads);
        Assert.Equal(BusinessBlockState.Stopped, Assert.Single(controller.Read().Blocks).State);
    }

    [Fact]
    public void FailedFinalThreadSampleStillReleasesItsMetricsRegistration()
    {
        var metrics = new BusinessBlockMetrics();
        var registration = metrics.RegisterCurrentThread("failed-final-sample");
        var threads = (IDictionary)Field(metrics, "activeThreads");
        var state = threads.Values.Cast<object>().Single();
        state.GetType().GetProperty("LastCpuTicks")!.SetValue(state, long.MaxValue);
        Assert.Throws<InvalidOperationException>(registration.Dispose);
        Assert.Empty(metrics.Read().ActiveThreads);
    }

    [Fact]
    public async Task RawInputShutdownReportsBothFailedPosts()
    {
        var host = new WindowsRawInputCaptureHost(WindowsRawInputDeviceKind.Mouse);
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() => release.Wait(TimeSpan.FromSeconds(10))) { IsBackground = true };
        // Substitute only private state; never start a real Raw Input registration.
        SetField(host, "thread", worker);
        SetField(host, "window", new IntPtr(-100));
        SetField(host, "threadId", uint.MaxValue);
        worker.Start();
        var stopped = (TaskCompletionSource<bool>)Field(host, "stopped");
        var stop = Task.Run(host.Stop);
        try
        {
            var error = await Assert.ThrowsAsync<WindowsRawInputCaptureException>(() => stop.WaitAsync(TimeSpan.FromSeconds(2)));
            var failures = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains("WM_CLOSE", failures[0].Message);
            Assert.Contains("WM_QUIT", failures[1].Message);
        }
        finally
        {
            stopped.TrySetResult(true);
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            try { await stop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (WindowsRawInputCaptureException) { }
        }
    }

    [Fact]
    public async Task RawInputShutdownHasAVisibleFiniteDeadline()
    {
        var host = new WindowsRawInputCaptureHost(WindowsRawInputDeviceKind.Mouse);
        var stopped = (TaskCompletionSource<bool>)Field(host, "stopped");
        var stop = Task.Run(() => Record.Exception(host.Stop));
        try
        {
            var error = Assert.IsType<WindowsRawInputCaptureException>(await stop.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.IsType<TimeoutException>(error.InnerException);
            Assert.Contains("5 seconds", error.Message);
        }
        finally
        {
            // Also release the old implementation when this regression runs against it.
            stopped.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static object Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}

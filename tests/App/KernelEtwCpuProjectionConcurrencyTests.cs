using System.Collections.Frozen;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwCpuProjectionConcurrencyTests
{
    [Fact]
    public async Task ConcurrentCpuProjectionCannotReplaceAnAcceptedOriginalWindowWithEmptyRows()
    {
        var origin = DateTimeOffset.UtcNow;
        using var resource = new NativeKernelEtwTestResource(
            NativeKernelEtwTestResource.SmallConfiguration(1000, 1), origin);
        using var broker = new ReceiptBarrierBroker(resource);
        CpuResidencyCaptureConfiguration plan = new(1000, 32,
            new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(),
            "concurrent native CPU projection fixture");
        using var owner = new EtwCpuResidencyCaptureOwner(broker, () => plan);
        owner.EnsureStarted();
        Assert.False(owner.IsWindowActive);
        // The controlled observation is due at QPC zero, before either collector.
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        Assert.True(owner.IsWindowActive);
        resource.Publish(0);
        resource.ProcessStart(0, origin, 41, 11, origin.AddDays(-1).ToFileTime(), "fixture");
        resource.ThreadStart(0, origin, 41, 51);
        resource.CSwitch(0, origin, 0, 0, 0, 41, 51);
        resource.CSwitch(1000, origin.AddSeconds(1), 0, 41, 51, 0, 0);
        resource.Publish(1000);
        Assert.True(owner.Collect());
        var first = Assert.IsType<CpuCoreResidencySnapshot>(owner.Read());
        Assert.Equal(1000, Assert.Single(first.Processes).ExecutionTimeMilliseconds);
        Assert.Equal(origin.AddSeconds(1), first.MeasuredThrough);

        // A's second broker receipt is after its aggregate was read and after
        // the native lease was returned. No product hook or event fake is used.
        var collectionA = Task.Run(() =>
        {
            broker.PauseSecondReceiptForThisFlow();
            return owner.Collect();
        });
        bool acceptedA;
        CpuCoreResidencySnapshot? second = null;
        try
        {
            Assert.True(broker.Reached.Wait(TimeSpan.FromSeconds(5)), "Collection A did not reach its after-read broker receipt.");
            resource.CSwitch(1000, origin.AddSeconds(1), 0, 0, 0, 41, 51);
            resource.CSwitch(2000, origin.AddSeconds(2), 0, 41, 51, 0, 0);
            resource.Publish(2000);
            Assert.True(await owner.CollectAsync());
            second = Assert.IsType<CpuCoreResidencySnapshot>(owner.Read());
            Assert.Equal(origin.AddSeconds(2), second.MeasuredThrough);
            Assert.Equal(1000, Assert.Single(second.Processes).ExecutionTimeMilliseconds);
        }
        finally
        {
            broker.Resume.Set();
            acceptedA = await collectionA.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(acceptedA);
        var current = Assert.IsType<CpuCoreResidencySnapshot>(owner.Read());
        Assert.NotNull(second);
        // B accepted the newer complete window while A was paused. A's late
        // completion must replace it with neither empty rows nor its old window.
        Assert.Same(second, current);
        Assert.NotSame(first, current);
        Assert.Equal(origin.AddSeconds(1), current.MeasuredFrom);
        Assert.Equal(origin.AddSeconds(2), current.MeasuredThrough);
        var process = Assert.Single(current.Processes);
        Assert.Equal(41, process.ProcessId);
        Assert.Equal(1000, process.ExecutionTimeMilliseconds);
        Assert.Single(process.Threads);
        Assert.Same(second, owner.Read());
        Assert.Empty(resource.CallbackFailures);
    }

    private sealed class ReceiptBarrierBroker(NativeKernelEtwTestResource resource) : IKernelEtwSessionBroker, IDisposable
    {
        private sealed class Probe { internal int Receipts; }
        private readonly AsyncLocal<Probe?> currentProbe = new();
        internal ManualResetEventSlim Reached { get; } = new();
        internal ManualResetEventSlim Resume { get; } = new();
        internal void PauseSecondReceiptForThisFlow() => currentProbe.Value = new();
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
            => new ForwardedHold(this, resource.Acquire(request));
        public KernelEtwSessionSnapshot GetSnapshot() => Receive(resource.GetSnapshot());
        private KernelEtwSessionSnapshot Receive(KernelEtwSessionSnapshot snapshot)
        {
            if (currentProbe.Value is { } probe && ++probe.Receipts == 2)
            {
                Reached.Set();
                if (!Resume.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The deterministic CPU projection interleave was not released.");
            }
            return snapshot;
        }
        private sealed class ForwardedHold(ReceiptBarrierBroker owner, IKernelEtwSubscription hold) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.Receive(hold.GetSnapshot());
            public void Dispose() => hold.Dispose();
        }
        public void Dispose() { Resume.Set(); Reached.Dispose(); Resume.Dispose(); }
    }
}

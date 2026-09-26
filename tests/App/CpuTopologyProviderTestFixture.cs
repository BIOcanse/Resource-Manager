using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

internal sealed class CpuTopologyProviderTestFixture : IAsyncDisposable
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly HostManagerSamplingSubscriptionTestFixture sampling = new();

    public CpuTopologyProviderTestFixture()
    {
        Reader = new CpuTopologySnapshotProvider(
            Sampler, sampling.Owner, NullLogger<CpuTopologySnapshotProvider>.Instance);
    }

    internal ControlledSampler Sampler { get; } = new();
    internal CpuTopologySnapshotProvider Reader { get; }
    internal HostManagerSamplingSubscriptionOwner Owner => sampling.Owner;

    internal Subscription Subscribe(string id = "topology", TimeSpan? interval = null)
        => new(Reader, id, interval ?? TimeSpan.FromMilliseconds(100));

    internal (uint SourceCount, uint PersistentSourceCount, uint ItemCount, ulong IntervalMilliseconds) ReadDemand(
        int roleId = CpuTopologySnapshotProvider.SamplingRoleId)
        => sampling.Owner.Execute(roleId, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return (header.ActiveSourceCount, header.PersistentSourceCount, header.KnownItemCount,
                header.ItemOutputCount == 0 ? 0 : items[0].EffectiveIntervalMilliseconds);
        });

    public async ValueTask DisposeAsync()
    {
        var stopping = Reader.StopAsync(CancellationToken.None);
        Sampler.Complete(new OperationCanceledException("Fixture shutdown."));
        await stopping.WaitAsync(Deadline);
        Reader.Dispose();
        sampling.Dispose();
        Sampler.Dispose();
    }

    internal static CpuTopologySnapshot CreateSnapshot(double usage) => new(
        DateTimeOffset.UtcNow,
        "Subscription Test CPU",
        new CpuSpecificationModel("Subscription Test CPU", "Test", "Test", 1, 1, 3000, 2000, 256, 1024, "test"),
        "test", "test", CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
        CpuTopologyVisualLayoutKinds.Grid, "test", 1, 1, 1, false,
        [new CpuCcdModel("ccd:0", 0, "CCD 0", usage, [0], [0], "test")],
        [new CpuPhysicalCoreModel("core:0", 0, "Core 0", "ccd:0", 0, 1, usage, [0], [])],
        [new CpuLogicalProcessorModel(0, 0, 0, "core:0", "ccd:0", 1, usage, true)],
        []);

    internal sealed class ControlledSampler : ICpuTopologySampler, IDisposable
    {
        private readonly BlockingCollection<object> completions = new();
        private readonly Channel<int> attempts = Channel.CreateUnbounded<int>();
        private int captureCount;

        internal int CaptureCount => Volatile.Read(ref captureCount);

        public CpuTopologySnapshot CaptureSnapshot()
        {
            attempts.Writer.TryWrite(Interlocked.Increment(ref captureCount));
            if (!completions.TryTake(out var result, Deadline))
            {
                throw new TimeoutException("Test did not complete the native capture boundary.");
            }
            if (result is Exception exception)
            {
                throw exception;
            }
            return (CpuTopologySnapshot)result;
        }

        public CpuTopologySnapshot CaptureTopology()
            => throw new InvalidOperationException("The subscription worker must capture the full value.");

        internal Task<int> WaitForCaptureAsync()
            => attempts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        internal void Complete(object result) => completions.Add(result);

        public void Dispose() => completions.Dispose();
    }

    internal sealed class Subscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly IAsyncEnumerator<CpuTopologySnapshot?> enumerator;
        private Task<CpuTopologySnapshot?>? pending;
        private bool disposed;

        internal Subscription(ICpuTopologyReader reader, string id, TimeSpan interval)
        {
            enumerator = reader.SubscribeAsync(id, interval, cancellation.Token).GetAsyncEnumerator();
        }

        internal Task<CpuTopologySnapshot?> Next()
        {
            Assert.False(pending is { IsCompleted: false });
            pending = MoveNextAsync();
            return pending;
        }

        private async Task<CpuTopologySnapshot?> MoveNextAsync()
        {
            Assert.True(await enumerator.MoveNextAsync());
            return enumerator.Current;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            await cancellation.CancelAsync();
            if (pending is not null)
            {
                try
                {
                    await pending.WaitAsync(Deadline);
                }
                catch (OperationCanceledException)
                {
                }
            }
            await enumerator.DisposeAsync();
            cancellation.Dispose();
        }
    }
}

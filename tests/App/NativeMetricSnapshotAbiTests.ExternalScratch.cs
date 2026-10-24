using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Native;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeMetricSnapshotAbiTests
{
    [Fact]
    public void CompletionWritesTheRealRegionAndFailuresKeepItsPinUntilSuccessfulAbort()
    {
        var configuration = CreateConfiguration();
        MetricScratchRecording? region = null;
        using var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => region = new(length, alignment));
        var call = new ScratchCalls(session);
        var header = call.Begin();
        Assert.NotNull(region);
        Assert.Equal(2, region.Acquires); // Plan ends its own use before Begin acquires.
        Assert.Equal(1, region.Returns);
        Assert.Equal(1, region.Pins - region.Unpins);
        call.Submit(ulong.MaxValue);
        var copied = region.ReadBound<NativeMetricSnapshotObservationInput>(0);
        Assert.Equal(ulong.MaxValue, copied.ValueBits);
        Assert.Equal(1UL, copied.RuleHandle);
        Assert.Equal(1UL, copied.MetricHandle);
        Assert.Equal(1UL, copied.SourceObservationSequence);
        var invalid = call.Finalize;
        invalid.CatalogGeneration++;
        Assert.Equal(NativeMetricSnapshotStatus.AbiMismatch, session.FinalizeCompletion(in invalid));
        Assert.Equal(NativeMetricSnapshotStatus.AbiMismatch, session.AbortCompletion(in invalid));
        Assert.Equal(NativeMetricSnapshotStatus.Unavailable, session.BeginCompletion(in header));
        Assert.Equal(2, region.Acquires);
        Assert.Equal(1, region.Returns);
        Assert.Equal(1, region.Pins - region.Unpins);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.AbortCompletion(in call.Finalize));
        Assert.Equal(2, region.Returns);
        Assert.Equal(region.Pins, region.Unpins);
        Assert.Equal(["use", "pin", "complete", "unpin", "endUse", "use", "pin", "complete", "unpin", "endUse"], region.Events);
    }

    [Fact]
    public void SuccessfulFinalizeSurvivesScratchPoisonAndNextPlanRefillsTheSameResource()
    {
        var configuration = CreateConfiguration();
        MetricScratchRecording? region = null;
        using var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => region = new(length, alignment));
        var call = new ScratchCalls(session);
        call.Begin(); call.Submit(0);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in call.Finalize));
        var first = call.Read();
        Assert.Equal(0UL, first.ValueBits);
        Assert.Equal((uint)NativeMetricSnapshotMetricStatus.Current, first.Status);
        call.Begin(2); call.Submit(ulong.MaxValue, 2);
        Assert.Equal(ulong.MaxValue, region!.ReadBound<NativeMetricSnapshotObservationInput>(0).ValueBits);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in call.Finalize));
        var next = call.Read();
        Assert.Equal(ulong.MaxValue, next.ValueBits);
        Assert.Equal((uint)NativeMetricSnapshotMetricStatus.Current, next.Status);
        Assert.Equal(4, region.Acquires);
        Assert.Equal(4, region.Returns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeRetainsFailedEndUseOrRetirementAndCanRetryWithoutDoubleUnpin(bool retirement)
    {
        var configuration = CreateConfiguration();
        MetricScratchRecording? region = null;
        var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => region = new(length, alignment));
        var call = new ScratchCalls(session);
        call.Begin(); call.Submit(17);
        region!.BeforeUnpin = () => Assert.Throws<ObjectDisposedException>(() => session.QueryHeader(out _));
        region.FailReturn = !retirement;
        region.FailRetire = retirement;
        var error = Assert.Throws<InvalidOperationException>(() => session.Dispose());
        Assert.Same(retirement ? region.RetireFailure : region.ReturnFailure, error);
        Assert.Equal(region.Pins, region.Unpins);
        Assert.Throws<ObjectDisposedException>(() => session.QueryHeader(out _));
        region.FailReturn = false; region.FailRetire = false;
        session.Dispose(); session.Dispose();
        Assert.Equal(2, region.Returns);
        Assert.Equal(2, region.Unpins);
    }

    [Fact]
    public void FailedBeginReturnsTheLoanAndAcquireFailureDoesNotFallBack()
    {
        var configuration = CreateConfiguration();
        MetricScratchRecording? region = null;
        using var session = new NativeMetricSnapshotSession(in configuration,
            (length, alignment) => region = new(length, alignment));
        var invalid = new NativeMetricSnapshotCompletionHeader();
        Assert.Equal(NativeMetricSnapshotStatus.AbiMismatch, session.BeginCompletion(in invalid));
        Assert.Equal(1, region!.Acquires); Assert.Equal(1, region.Returns);
        region.FailAcquire = true;
        Assert.Throws<InvalidOperationException>(() => session.BeginCompletion(in invalid));
        Assert.Equal(1, region.Acquires); Assert.Equal(1, region.Returns);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.QueryHeader(out var header));
        Assert.Equal((uint)NativeMetricSnapshotPhase.Empty, header.Phase);
        region.FailAcquire = false;
    }

    internal sealed class ScratchCalls
    {
        internal readonly NativeMetricSnapshotSession Session;
        internal NativeMetricSnapshotFinalizeInput Finalize;
        internal ScratchCalls(NativeMetricSnapshotSession session)
        {
            Session = session;
            NativeMetricSnapshotSourcePolicyInput[] sources =
                [new() { StructSize = Size<NativeMetricSnapshotSourcePolicyInput>(), SourceHandle = 1,
                    SourceRole = (uint)NativeMetricSnapshotSourceRole.Metrics,
                    Priority = 1, CapabilityMask = 1, SemanticFingerprint = 11 }];
            NativeMetricSnapshotMetricDefinitionInput[] rules =
                [new() { StructSize = Size<NativeMetricSnapshotMetricDefinitionInput>(), RuleHandle = 1, MetricHandle = 1,
                    SourceHandle = 1, ScopeHandle = 1, MetricKind = (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
                    ScopeKind = (uint)NativeMetricSnapshotScopeKind.Host, ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64, CapabilityMask = 1,
                    SourcePriority = 1, MaximumValueBits = ulong.MaxValue, SemanticFingerprint = 22 }];
            var replace = new NativeMetricSnapshotCatalogReplaceInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotCatalogReplaceInput>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = 1,
                CommandAtMilliseconds = 100, SourceCount = 1, RuleCount = 1, MetricCount = 1,
                SourceIndexCapacity = session.Capacity.SourceIndexCapacity,
                MetricIndexCapacity = session.Capacity.MetricIndexCapacity,
                RuleIndexCapacity = session.Capacity.RuleIndexCapacity,
                SemanticFingerprint = NativeMetricSnapshotSession.CalculateCatalogFingerprint(sources, rules)
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.ReplaceCatalog(in replace, sources, rules));
        }

        internal NativeMetricSnapshotCompletionHeader Begin(ulong sequence = 1)
        {
            var plan = new NativeMetricSnapshotPlanInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotPlanInput>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = sequence * 2,
                PlanEpoch = sequence, CommandAtMilliseconds = sequence * 1000,
                RequestedMetricCount = 1, SourceModeCount = 1,
                SourcePlanCapacity = Session.Capacity.SourcePlanCapacity, MetricPlanCapacity = Session.Capacity.MetricPlanCapacity
            };
            var sources = new NativeMetricSnapshotSourcePlanOutput[Session.Capacity.SourcePlanCapacity];
            Assert.Equal(NativeMetricSnapshotStatus.Ok, Session.Plan(in plan,
                [new() { StructSize = Size<NativeMetricSnapshotPlanMetricInput>(), MetricHandle = 1 }],
                [new() { StructSize = Size<NativeMetricSnapshotSourceModeInput>(), SourceHandle = 1, CapabilityGeneration = 1,
                    ZoneMode = (uint)NativeMetricSnapshotZoneMode.Normal, Availability = (uint)NativeMetricSnapshotSourceAvailability.Available }],
                out _, sources, new NativeMetricSnapshotMetricPlanOutput[Session.Capacity.MetricPlanCapacity]));
            var header = new NativeMetricSnapshotCompletionHeader
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotCompletionHeader>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = sequence * 2 + 1,
                PlanEpoch = sequence, SourceHandle = 1, SourceIncarnation = 1, SourceObservationSequence = sequence,
                CapabilityGeneration = sources[0].CapabilityGeneration, PlanTokenFingerprint = sources[0].PlanTokenFingerprint,
                CommandAtMilliseconds = sequence * 1000, CapturedAtMilliseconds = sequence * 1000,
                RequestedRuleCount = 1, ObservationCount = 1, Status = (uint)NativeMetricSnapshotSourceStatus.Complete
            };
            Finalize = new() { AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotFinalizeInput>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, OperationEpoch = header.OperationEpoch,
                PlanEpoch = sequence, SourceHandle = 1, CommandAtMilliseconds = sequence * 1000 };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, Session.BeginCompletion(in header));
            return header;
        }

        internal void Submit(ulong value, ulong sequence = 1)
        {
            Assert.Equal(NativeMetricSnapshotStatus.Ok, Session.SubmitRequested(
                [new() { StructSize = Size<NativeMetricSnapshotRequestedMetricInput>(), RuleHandle = 1 }]));
            Assert.Equal(NativeMetricSnapshotStatus.Ok, Session.SubmitObservations(
                [new() { StructSize = Size<NativeMetricSnapshotObservationInput>(), RuleHandle = 1, MetricHandle = 1,
                    SourceHandle = 1, ScopeHandle = 1, SourceObservationSequence = sequence,
                    ObservedAtMilliseconds = sequence * 1000, CapabilityMask = 1,
                    Status = (uint)NativeMetricSnapshotObservationStatus.Current, ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64,
                    ValidMask = (ulong)NativeMetricSnapshotObservationValidity.Required | (ulong)NativeMetricSnapshotObservationValidity.Value, ValueBits = value }]));
        }

        internal NativeMetricSnapshotMetricOutput Read()
        {
            Assert.Equal(NativeMetricSnapshotStatus.Ok, Session.QueryHeader(out var header));
            var input = new NativeMetricSnapshotReadInput
            { AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotReadInput>(),
                ConfigurationGeneration = 1, CatalogGeneration = 1, CatalogFingerprint = header.CatalogFingerprint,
                StateRevision = header.StateRevision, CommittedGeneration = header.CommittedGeneration };
            var metrics = new NativeMetricSnapshotMetricOutput[Session.Capacity.MetricCapacity];
            Assert.Equal(NativeMetricSnapshotStatus.Ok, Session.Read(in input,
                new NativeMetricSnapshotSourceOutput[Session.Capacity.SourceCapacity], metrics,
                new NativeMetricSnapshotGpuInventoryOutput[Session.Capacity.GpuAdapterCapacity],
                new NativeMetricSnapshotRuleStateOutput[Session.Capacity.RuleCapacity]));
            return metrics[0];
        }
        private static uint Size<T>() where T : unmanaged => checked((uint)Unsafe.SizeOf<T>());
    }
    // A real caller-owned region. Counters observe pin/use lifetime, not production branches.
    internal sealed unsafe class MetricScratchRecording(ulong length, uint alignment) : INativeMetricSnapshotScratch
    {
        private readonly byte[] bytes = new byte[checked((int)(length + alignment - 1))];
        private readonly Region region = new();
        private bool active;
        internal readonly List<string> Events = [];
        internal int Acquires;
        internal int Returns;
        internal int Pins;
        internal int Unpins;
        internal int Retires;
        internal bool FailAcquire;
        internal bool FailReturn;
        internal bool FailRetire;
        internal Action? BeforeUnpin;
        internal readonly InvalidOperationException ReturnFailure = new("recorded metric endUse failure");
        internal readonly InvalidOperationException RetireFailure = new("recorded metric retirement failure");
        public ulong Length => checked((ulong)bytes.Length);

        internal static INativeMetricSnapshotScratch Ordinary(ulong bytes, uint alignment)
            => new MetricScratchRecording(bytes, alignment);

        public NativeMetricSnapshotScratchLease Acquire()
        {
            if (FailAcquire) throw new InvalidOperationException("recorded metric acquire failure");
            Assert.False(active);
            active = true;
            Array.Fill(bytes, (byte)0xA5);
            region.Owner = this;
            Acquires++;
            Events.Add("use");
            return new Reading(this);
        }

        internal T ReadBound<T>(ulong offset) where T : unmanaged
        {
            Assert.True(active);
            Assert.Equal(Pins, Unpins + 1);
            var first = checked((nuint)region.Handle.AddrOfPinnedObject());
            var address = checked(first + alignment - 1) & ~checked((nuint)(alignment - 1));
            Assert.True(offset + (ulong)sizeof(T) <= length);
            return *(T*)checked(address + (nuint)offset);
        }

        public void Dispose()
        {
            Assert.False(active);
            Assert.Equal(Pins, Unpins);
            if (FailRetire) throw RetireFailure;
            Retires++;
            Events.Add("retire");
        }

        private sealed class Reading(MetricScratchRecording owner) : NativeMetricSnapshotScratchLease
        {
            private bool disposed;
            internal override ulong Length => owner.Length;
            internal override MemoryHandle Pin() => owner.region.Pin();
            internal override void Complete() => owner.Events.Add("complete");
            public override void Dispose()
            {
                if (disposed) return;
                Assert.Equal(owner.Pins, owner.Unpins);
                if (owner.FailReturn) throw owner.ReturnFailure;
                owner.active = false;
                owner.Returns++;
                owner.Events.Add("endUse");
                disposed = true;
            }
        }

        private sealed class Region : MemoryManager<byte>
        {
            internal MetricScratchRecording Owner = null!;
            internal GCHandle Handle;
            public override Span<byte> GetSpan() => Owner.bytes;
            public override MemoryHandle Pin(int elementIndex = 0)
            {
                Assert.False(Handle.IsAllocated);
                Assert.InRange(elementIndex, 0, Owner.bytes.Length - 1);
                Handle = GCHandle.Alloc(Owner.bytes, GCHandleType.Pinned);
                Owner.Pins++;
                Owner.Events.Add("pin");
                return new MemoryHandle((byte*)Handle.AddrOfPinnedObject() + elementIndex, default, this);
            }
            public override void Unpin()
            {
                Owner.BeforeUnpin?.Invoke();
                Handle.Free();
                Owner.Unpins++;
                Owner.Events.Add("unpin");
            }
            protected override void Dispose(bool disposing) { }
        }
    }
}

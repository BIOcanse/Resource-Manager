using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayOwnerLibraryIntegrationTests
{
    [Fact]
    public void CompleteOwnerAndPassiveInputsAreLibraryOwnedAndDisposalIsExactlyOnce()
    {
        var session = new Session();
        var owner = Owner(session);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(NativeDisplayCoordinatorWorkspace).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(NativeDisplayCoordinatorConfiguration).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(DeviceTopologyOemDisplayConnectorProfile).Assembly);
        Assert.Equal(0, session.BeginCount);
        owner.Dispose(); owner.Dispose();
        Assert.Equal(1, session.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => owner.Refresh([]));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void FailureBeforeFinalizeAbortsButFailureAfterFinalizeDoesNotRollback(bool afterFinalize, int aborts)
    {
        var session = new Session { FailSubmit = !afterFinalize, FailRead = afterFinalize };
        using var owner = Owner(session);
        Assert.Throws<InvalidOperationException>(() => owner.Refresh([]));
        Assert.Equal(aborts, session.AbortCount);
        Assert.Equal(afterFinalize ? 1 : 0, session.FinalizeCount);
    }

    [Fact]
    public void ConstructorImportFailureDestroysSessionAndDoesNotCapture()
    {
        var session = new Session();
        Assert.Throws<InvalidDataException>(() => Owner(session,
            new(_ => throw new InvalidDataException("recorded broken envelope"), (_, _) => { })));
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, session.BeginCount);
    }

    [Fact]
    public async Task CanonicalBackendAndCachedProjectionUseTheSameCommittedOwnerWithoutRecapturing()
    {
        var session = new Session();
        using var owner = Owner(session);
        var first = owner.Refresh([]);
        var backend = new InformationReadWriteBackend(() => owner.ReadFields(Identity), token => {
            token.ThrowIfCancellationRequested(); owner.Refresh([]);
            return ValueTask.FromResult(owner.ReadFields(Identity));
        }, static _ => new(true, null, []));
        var field = backend.Catalog.Fields.First().Field;
        var reading = backend.ReadCached(field);
        Assert.Equal(1, session.BeginCount);
        Assert.Single(owner.ReadCurrent().Current.DisplayPaths);
        Assert.Same(first, owner.Refresh([]));
        Assert.Equal(2, session.BeginCount);
        await backend.CollectAsync([field], default);
        Assert.Equal(3, session.BeginCount);
        Assert.Equal(reading.Field, backend.ReadCached(field).Field);
        Assert.Equal(3, session.BeginCount);
    }

    [Fact]
    public void SaveFailureLeavesFinalizedCurrentAndDoesNotAbort()
    {
        var session = new Session();
        using var owner = Owner(session, new(_ => null, (_, _) => throw new IOException("recorded save failure")));
        Assert.Throws<IOException>(() => owner.Refresh([]));
        Assert.Equal(0, session.AbortCount);
        Assert.Equal(1, session.FinalizeCount);
        Assert.Single(owner.ReadCurrent().Current.DisplayPaths);
    }

    private static string Identity(DeviceTopologyDisplayPath path) => path.MonitorDevicePath!;
    private static NativeDisplayCoordinatorWorkspace Owner(Session session, NativeDisplayCoordinatorPersistenceCallbacks? persistence = null)
        => new(new NativeDisplayCoordinatorConfiguration { Generation = 1 }, session,
            () => new([new DeviceTopologyDisplayPath(1, 2, 3, 4, 10, 0, "Recorded", @"DISPLAY\RECORDED\1",
                1920, 1080, 60, 1, true, true, "PCI\\RECORDED")], null), persistence);

    private sealed class Session : INativeDisplayCoordinatorSession
    {
        private NativeDisplayHandle128 handle;
        private long captured;
        public int DisposeCount, BeginCount, AbortCount, FinalizeCount;
        public bool FailSubmit, FailRead;
        public NativeDisplayCoordinatorCapacity Capacity => new() { MaximumObservationCount = 8, MaximumTextBindingCount = 32, MaximumTextByteCount = 4096 };
        public NativeDisplayCoordinatorStatus BeginRefresh(in NativeDisplayRefreshInput input)
        { BeginCount++; captured = input.CapturedUtcMilliseconds; return NativeDisplayCoordinatorStatus.Ok; }
        public NativeDisplayCoordinatorStatus SubmitSource(in NativeDisplaySourceBatchHeader header,
            ReadOnlySpan<NativeDisplayFact> facts, ReadOnlySpan<NativeDisplayTextInput> texts, ReadOnlySpan<byte> bytes)
        {
            if (FailSubmit) return (NativeDisplayCoordinatorStatus)1;
            if (header.SourceId == (uint)NativeDisplaySource.DisplayConfig && !facts.IsEmpty) handle = facts[0].PayloadHandle;
            return NativeDisplayCoordinatorStatus.Ok;
        }
        public NativeDisplayCoordinatorStatus FinalizeRefresh(in NativeDisplayFinalizeInput input)
        { FinalizeCount++; return NativeDisplayCoordinatorStatus.Ok; }
        public NativeDisplayCoordinatorStatus AbortRefresh(in NativeDisplayAbortInput input)
        { AbortCount++; return NativeDisplayCoordinatorStatus.Ok; }
        public NativeDisplayCoordinatorStatus Snapshot(out NativeDisplaySnapshotOutput output)
        {
            output = new() { AbiVersion = NativeDisplayCoordinatorAbi.Version,
                StructSize = (uint)Unsafe.SizeOf<NativeDisplaySnapshotOutput>(), ConfigurationGeneration = 1,
                ContentGeneration = 1, StateRevision = 1, NodeCount = 1, Phase = (uint)NativeDisplayCoordinatorPhase.Ready,
                LastSuccessUtcMilliseconds = captured, CurrentSourceMask = 1 };
            return NativeDisplayCoordinatorStatus.Ok;
        }
        public NativeDisplayCoordinatorStatus ReadNodes(in NativeDisplayReadInput input, Span<NativeDisplayNodeOutput> output)
        {
            if (FailRead) return (NativeDisplayCoordinatorStatus)1;
            output[0] = new() { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = handle };
            return NativeDisplayCoordinatorStatus.Ok;
        }
        public NativeDisplayCoordinatorStatus ExportPersistence(in NativeDisplayPersistenceInput input,
            out NativeDisplayPersistenceHeader header, Span<NativeDisplayFact> facts, Span<NativeDisplayTextInput> texts, Span<byte> bytes)
        { header = new() { OperationEpoch = input.OperationEpoch }; return NativeDisplayCoordinatorStatus.Ok; }
        public NativeDisplayCoordinatorStatus ImportPersistence(in NativeDisplayPersistenceInput input,
            in NativeDisplayPersistenceHeader header, ReadOnlySpan<NativeDisplayFact> facts, ReadOnlySpan<NativeDisplayTextInput> texts, ReadOnlySpan<byte> bytes)
            => NativeDisplayCoordinatorStatus.Ok;
        public void Dispose() => DisposeCount++;
    }
}

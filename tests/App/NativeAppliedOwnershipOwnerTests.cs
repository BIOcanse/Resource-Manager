using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.Ledger;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class NativeAppliedOwnershipOwnerTests
{
    [Fact]
    public async Task MissingLedgerCreatesAndPersistsCanonicalImageBeforeReturningReady()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ownership.bin");
        var session = new FakeSession(CreateImage(0x11));
        var factory = new FakeFactory(session);
        var store = new NativeAppliedOwnershipFileStore(path);

        await using var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            store,
            factory,
            CreateConfiguration(),
            CreateOpenConfiguration(),
            NativeAppliedOwnershipTestStorage.Create,
            CancellationToken.None);

        Assert.Equal(NativeAppliedOwnershipOwnerState.Ready, owner.State);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(CreateImage(0x11), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExistingLedgerUsesNativeStartupDecodeInsteadOfCreatingSecondAuthority()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ownership.bin");
        var image = CreateImage(0x22);
        await File.WriteAllBytesAsync(path, image);
        var session = new FakeSession(image);
        var factory = new FakeFactory(session)
        {
            OpenStatus = NativeAppliedOwnershipStatus.Ok
        };
        var store = new NativeAppliedOwnershipFileStore(path);

        await using var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            store,
            factory,
            CreateConfiguration(),
            CreateOpenConfiguration(),
            NativeAppliedOwnershipTestStorage.Create,
            CancellationToken.None);

        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(image, factory.LastOpenedImage);
        Assert.Equal(NativeAppliedOwnershipOwnerState.Ready, owner.State);
    }

    [Fact]
    public async Task SuccessfulMutationsCrossDurabilityBarrierAndRejectedMutationsDoNotWrite()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ownership.bin");
        var session = new FakeSession(CreateImage(0x31));
        var factory = new FakeFactory(session);
        var store = new NativeAppliedOwnershipFileStore(path);
        await using var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            store,
            factory,
            CreateConfiguration(),
            CreateOpenConfiguration(),
            NativeAppliedOwnershipTestStorage.Create,
            CancellationToken.None);

        session.NextMutationImage = CreateImage(0x32);
        var promoted = await owner.PromoteAsync(default, CancellationToken.None);
        Assert.Equal(NativeAppliedOwnershipStatus.Ok, promoted);
        Assert.Equal(CreateImage(0x32), await File.ReadAllBytesAsync(path));

        session.PromoteStatus = NativeAppliedOwnershipStatus.StaleRevision;
        session.NextMutationImage = CreateImage(0x33);
        var rejected = await owner.PromoteAsync(default, CancellationToken.None);
        Assert.Equal(NativeAppliedOwnershipStatus.StaleRevision, rejected);
        Assert.Equal(CreateImage(0x32), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MutationsAreForwardedWithoutManagedIdentityOrCasRewriting()
    {
        using var directory = new TemporaryDirectory();
        var session = new FakeSession(CreateImage(0x38));
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(
                Path.Combine(directory.Path, "ownership.bin")),
            new FakeFactory(session),
            CreateConfiguration(),
            CreateOpenConfiguration(),
            NativeAppliedOwnershipTestStorage.Create,
            CancellationToken.None);
        await using (owner)
        {
            var promote = new NativeAppliedOwnershipPromoteInput
            {
                ExpectedLedgerRevision = 101,
                Primary = new NativeAppliedOwnershipPrimaryIdentity
                {
                    Scope = 2,
                    SoftwareId = 102
                },
                Payload = new NativeAppliedOwnershipDurablePayloadReference
                {
                    Slot = 103,
                    Generation = 104,
                    Length = 105,
                    DigestLow = 106,
                    DigestHigh = 107
                }
            };
            var transition = new NativeAppliedOwnershipTransitionInput
            {
                ExpectedLedgerRevision = 201,
                Cas = new NativeAppliedOwnershipCas
                {
                    ExpectedRecordRevision = 202,
                    JournalInstanceLow = 203,
                    JournalInstanceHigh = 204
                },
                NewCurrentGrades = new NativeAppliedOwnershipCurrentGrades
                {
                    ValidMask = 4,
                    GpuGrade = 3
                }
            };
            var remove = new NativeAppliedOwnershipRemoveInput
            {
                ExpectedLedgerRevision = 301,
                Cas = new NativeAppliedOwnershipCas
                {
                    ExpectedRecordRevision = 302,
                    JournalInstanceLow = 303,
                    JournalInstanceHigh = 304
                },
                RemovedAtUtcMilliseconds = 305
            };

            Assert.Equal(
                NativeAppliedOwnershipStatus.Ok,
                await owner.PromoteAsync(promote, CancellationToken.None));
            Assert.Equal(
                NativeAppliedOwnershipStatus.Ok,
                await owner.TransitionAsync(transition, CancellationToken.None));
            Assert.Equal(
                NativeAppliedOwnershipStatus.Ok,
                await owner.RemoveAsync(remove, CancellationToken.None));

            Assert.Equal(101UL, session.LastPromote.ExpectedLedgerRevision);
            Assert.Equal(102UL, session.LastPromote.Primary.SoftwareId);
            Assert.Equal(103U, session.LastPromote.Payload.Slot);
            Assert.Equal(107UL, session.LastPromote.Payload.DigestHigh);
            Assert.Equal(201UL, session.LastTransition.ExpectedLedgerRevision);
            Assert.Equal(202UL, session.LastTransition.Cas.ExpectedRecordRevision);
            Assert.Equal(204UL, session.LastTransition.Cas.JournalInstanceHigh);
            Assert.Equal(3, session.LastTransition.NewCurrentGrades.GpuGrade);
            Assert.Equal(301UL, session.LastRemove.ExpectedLedgerRevision);
            Assert.Equal(302UL, session.LastRemove.Cas.ExpectedRecordRevision);
            Assert.Equal(304UL, session.LastRemove.Cas.JournalInstanceHigh);
            Assert.Equal(305UL, session.LastRemove.RemovedAtUtcMilliseconds);
        }
    }

    [Fact]
    public async Task PersistenceFailureFaultsOwnerAndPreventsFurtherMutation()
    {
        using var directory = new TemporaryDirectory();
        var session = new FakeSession(CreateImage(0x41));
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(
                Path.Combine(directory.Path, "ownership.bin")),
            new FakeFactory(session),
            CreateConfiguration(),
            CreateOpenConfiguration(),
            NativeAppliedOwnershipTestStorage.Create,
            CancellationToken.None);
        await using (owner)
        {
            session.EncodeStatus = NativeAppliedOwnershipStatus.InvalidArgument;

            await Assert.ThrowsAsync<NativeAppliedOwnershipPersistenceException>(
                () => owner.TransitionAsync(default, CancellationToken.None));

            Assert.Equal(NativeAppliedOwnershipOwnerState.PersistenceFaulted, owner.State);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.RemoveAsync(default, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SnapshotIsAnExactCopyOfNativeRecords()
    {
        using var directory = new TemporaryDirectory();
        var session = new FakeSession(CreateImage(0x51))
        {
            RequireZeroSnapshotInputs = true,
            SnapshotRecords =
            [
                new NativeAppliedOwnershipRecord
                {
                    RecordRevision = 17,
                    Primary = new NativeAppliedOwnershipPrimaryIdentity
                    {
                        Scope = (uint)NativeAppliedOwnershipScope.Process,
                        TargetId = 101,
                        ProcessStartKey = 202,
                        ProcessId = 303
                    }
                }
            ]
        };
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(
                Path.Combine(directory.Path, "ownership.bin")),
            new FakeFactory(session),
            CreateConfiguration(),
            CreateOpenConfiguration(),
            NativeAppliedOwnershipTestStorage.Create,
            CancellationToken.None);
        await using (owner)
        {
            var snapshot = await owner.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(1U, snapshot.Header.EntryCount);
            var record = Assert.Single(snapshot.Records);
            Assert.Equal(17UL, record.RecordRevision);
            Assert.Equal(101UL, record.Primary.TargetId);
            Assert.Equal(202UL, record.Primary.ProcessStartKey);
            Assert.Equal(303U, record.Primary.ProcessId);

            session.SnapshotRecords[0].RecordRevision = 99;
            var repeatedSnapshot = await owner.ReadSnapshotAsync(CancellationToken.None);

            Assert.Equal(17UL, snapshot.Records[0].RecordRevision);
            Assert.Equal(99UL, repeatedSnapshot.Records[0].RecordRevision);
        }
    }

    [Fact]
    public async Task RealNativeSnapshotUsesOneStableRegionAndRefillsAfterIdleRelease()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var create = LargeConfiguration();
        create.MaximumImageBytes = 16384;
        create.MaximumResidentBytes = 65536;
        var open = LargeOpenConfiguration();
        open.MaximumImageBytes = create.MaximumImageBytes;
        open.MaximumResidentBytes = create.MaximumResidentBytes;
        await using var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(Path.Combine(directory.Path, "ownership.bin")),
            new NativeAppliedOwnershipSessionFactory(), create, open,
            length => resource = ledger.CreateScratch(length), CancellationToken.None);
        Assert.NotNull(resource);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes); // Startup's one-use image is ordinary.
        var first = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Empty(first.Records);
        Assert.Equal(resource!.Length, ledger.ReadTable().RegisteredBytes);
        var uid = resource.Uid;
        ulong allocation;
        using (var lease = resource.Acquire()) allocation = lease.AllocationNumber;
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, ledger.Tick().Released);
        var second = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Equal(first.Header.LedgerInstanceLow, second.Header.LedgerInstanceLow);
        Assert.Equal(first.Header.LedgerRevision, second.Header.LedgerRevision);
        Assert.Equal(uid, resource.Uid);
        using (var lease = resource.Acquire()) Assert.NotEqual(allocation, lease.AllocationNumber);
        Assert.Empty(first.Records);

        var encodedPath = Path.Combine(directory.Path, "native-encode", "ownership.bin");
        var encodedStore = new NativeAppliedOwnershipFileStore(encodedPath);
        using var pathOwner = encodedStore.AcquireOwnerLease();
        using var encoder = new NativeAppliedOwnershipSession(in create);
        ResourceManagerLedgerOwner.ComputeScratch? encodedResource = null;
        using var encodedWorkspace = new NativeAppliedOwnershipWorkspace(encoder.Capacity,
            length => encodedResource = ledger.CreateScratch(length));
        using (var operation = encodedWorkspace.Acquire())
        {
            await encodedStore.PersistAsync(encoder, operation.Image, CancellationToken.None);
            operation.Complete();
        }
        var actualFile = await File.ReadAllBytesAsync(encodedPath);
        using var regionReader = encodedResource!.Acquire();
        var imageOffset = checked((ulong)encoder.Capacity.RecordCapacity * NativeAppliedOwnershipAbi.RecordSize);
        Assert.Equal(actualFile, regionReader.GetMemoryChunk(imageOffset, actualFile.Length).ToArray());
    }

    [Fact]
    public async Task SafeCloseInUseRetainsSessionAndPathUntilExplicitRetry()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var session = LargeSession(0x61);
        var store = new NativeAppliedOwnershipFileStore(Path.Combine(directory.Path, "ownership.bin"));
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(store, new FakeFactory(session),
            LargeConfiguration(), LargeOpenConfiguration(), length => resource = ledger.CreateScratch(length), CancellationToken.None);
        try
        {
            await owner.ReadSnapshotAsync(CancellationToken.None);
            using (var held = resource!.Acquire())
            {
                var failure = await Assert.ThrowsAsync<LedgerException>(() => owner.DisposeAsync().AsTask());
                Assert.Equal(LedgerStatus.InUse, failure.Status);
                Assert.Equal(NativeAppliedOwnershipOwnerState.Disposing, owner.State);
                Assert.Equal(0, session.DisposeCount);
                Assert.Throws<NativeAppliedOwnershipOwnerLeaseException>(() => store.AcquireOwnerLease());
                await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadSnapshotAsync(CancellationToken.None));
            }
            await owner.DisposeAsync();
            Assert.Equal(NativeAppliedOwnershipOwnerState.Disposed, owner.State);
            Assert.Equal(1, session.DisposeCount);
            using var nextPathOwner = store.AcquireOwnerLease();
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task AllocationRefusalPrecedesMutationAndKeepsAuthorityReady()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        RefusingAcquire? resource = null;
        var session = LargeSession(0x71);
        var path = Path.Combine(directory.Path, "ownership.bin");
        await using var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(path), new FakeFactory(session),
            LargeConfiguration(), LargeOpenConfiguration(),
            length => resource = new RefusingAcquire(ledger.CreateScratch(length)), CancellationToken.None);
        session.NextMutationImage = CreateImage(0x72);
        await Assert.ThrowsAsync<IOException>(() => owner.PromoteAsync(default, CancellationToken.None));
        Assert.NotNull(session.NextMutationImage);
        Assert.Equal(NativeAppliedOwnershipOwnerState.Ready, owner.State);
        Assert.Null(owner.PersistenceFailure);
        Assert.Equal(CreateImage(0x71), await File.ReadAllBytesAsync(path));
        resource!.Refuse = false;
        Assert.Equal(NativeAppliedOwnershipStatus.Ok, await owner.PromoteAsync(default, CancellationToken.None));
        Assert.Equal(CreateImage(0x72), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AsyncDurabilityKeepsLeaseAndConcurrentCloseWaitsForBothCallers()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var session = LargeSession(0x81);
        var path = Path.Combine(directory.Path, "ownership.bin");
        var store = new NativeAppliedOwnershipFileStore(path);
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(store, new FakeFactory(session),
            LargeConfiguration(), LargeOpenConfiguration(), ledger.CreateScratch, CancellationToken.None);
        var storeGate = (SemaphoreSlim)typeof(NativeAppliedOwnershipFileStore).GetField("gate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        await storeGate.WaitAsync();
        Task<NativeAppliedOwnershipStatus>? mutation = null;
        Task? firstClose = null, secondClose = null;
        try
        {
            session.NextMutationImage = CreateImage(0x82);
            mutation = owner.PromoteAsync(default, CancellationToken.None);
            Assert.False(mutation.IsCompleted);
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            firstClose = owner.DisposeAsync().AsTask();
            secondClose = owner.DisposeAsync().AsTask();
            Assert.False(firstClose.IsCompleted);
            Assert.False(secondClose.IsCompleted);
            Assert.Throws<NativeAppliedOwnershipOwnerLeaseException>(() => store.AcquireOwnerLease());
        }
        finally
        {
            storeGate.Release();
            if (mutation is not null) await mutation.WaitAsync(TimeSpan.FromSeconds(20));
            if (firstClose is not null) await firstClose.WaitAsync(TimeSpan.FromSeconds(20));
            if (secondClose is not null) await secondClose.WaitAsync(TimeSpan.FromSeconds(20));
            await owner.DisposeAsync();
        }
        Assert.Equal(CreateImage(0x82), await File.ReadAllBytesAsync(path));
        Assert.Equal(NativeAppliedOwnershipOwnerState.Disposed, owner.State);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task FailedEndUseIsKeptUntilExplicitCloseAndCloseFailureRemainsRetryable()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        RefusingEnd? resource = null;
        var session = LargeSession(0x91);
        var store = new NativeAppliedOwnershipFileStore(Path.Combine(directory.Path, "ownership.bin"));
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(store, new FakeFactory(session),
            LargeConfiguration(), LargeOpenConfiguration(),
            length => resource = new RefusingEnd(ledger.CreateScratch(length)), CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => owner.ReadSnapshotAsync(CancellationToken.None));
            Assert.Equal(1, resource!.EndAttempts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadSnapshotAsync(CancellationToken.None));
            Assert.Equal(1, resource.EndAttempts); // No silent retry at another operation's entry.
            ledger.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, ledger.Tick().Released);
            await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
            Assert.Equal(2, resource.EndAttempts);
            Assert.Equal(0, session.DisposeCount);
            Assert.Throws<NativeAppliedOwnershipOwnerLeaseException>(() => store.AcquireOwnerLease());
            await owner.DisposeAsync();
            Assert.Equal(3, resource.EndAttempts);
            Assert.Equal(1, session.DisposeCount);
            Assert.Equal(NativeAppliedOwnershipOwnerState.Disposed, owner.State);
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task HostCloseFailureRetainsOwnershipAndRetirementManagerForRetry()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        var session = LargeSession(0xA1);
        var store = new NativeAppliedOwnershipFileStore(Path.Combine(directory.Path, "ownership.bin"));
        var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(store, new FakeFactory(session),
            LargeConfiguration(), LargeOpenConfiguration(), length => resource = ledger.CreateScratch(length), CancellationToken.None);
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var retirements = new HostManagerAuthorityRetirementManager(directory.Path,
            WindowsHostManagerAuthorityRetirementStorage.Instance);
        var applied = new HostManagerAppliedOwnershipRuntime(
            new HostManagerAppliedOwnershipDeploymentRuntime(provider, deployment), retirements, ledger.CreateScratch);
        typeof(HostManagerAppliedOwnershipRuntime).GetField("owner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(applied, owner);
        var host = new HostManagerNativeActionTransactionRuntime(
            new HostManagerTransactionJournalDeploymentRuntime(provider, deployment), applied, retirements, ledger.CreateScratch);
        try
        {
            await owner.ReadSnapshotAsync(CancellationToken.None);
            using (var held = resource!.Acquire())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());
                Assert.Equal(NativeAppliedOwnershipOwnerState.Disposing, owner.State);
                Assert.Equal(0, session.DisposeCount);
                Assert.Equal(0, retirements.PendingCount(HostManagerAuthorityKind.AppliedOwnership));
                Assert.Throws<NativeAppliedOwnershipOwnerLeaseException>(() => store.AcquireOwnerLease());
                await Assert.ThrowsAsync<ObjectDisposedException>(() => applied.ReadSnapshotAsync(CancellationToken.None));
            }
            await host.DisposeAsync();
            await host.DisposeAsync();
            Assert.Equal(NativeAppliedOwnershipOwnerState.Disposed, owner.State);
            Assert.Equal(1, session.DisposeCount);
            using var nextPathOwner = store.AcquireOwnerLease();
        }
        finally { await host.DisposeAsync(); }
    }

    private sealed class RefusingEnd(IComputeScratch inner) : IComputeScratch
    {
        internal int EndAttempts;
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => new Lease(this, inner.Acquire());
        public void Dispose() => inner.Dispose();
        private sealed class Lease(RefusingEnd owner, ComputeScratchLease inner) : ComputeScratchLease
        {
            internal override ulong Length => inner.Length;
            internal override ulong AllocationNumber => inner.AllocationNumber;
            internal override bool IsLoading => inner.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => inner.GetMemoryChunk(offset, count);
            internal override void Complete() => inner.Complete();
            public override void Dispose()
            {
                if (++owner.EndAttempts <= 2) throw new IOException("recorded endUse refusal");
                inner.Dispose();
            }
        }
    }

    private static NativeAppliedOwnershipCreateConfiguration LargeConfiguration()
    {
        var value = CreateConfiguration();
        value.RecordCapacity = 16;
        value.PrimaryIndexCapacity = value.PayloadIndexCapacity = 32;
        value.MaximumResidentBytes = 8192;
        return value;
    }

    private static NativeAppliedOwnershipOpenConfiguration LargeOpenConfiguration()
    {
        var value = CreateOpenConfiguration();
        value.MaximumRecordCapacity = 16;
        value.MaximumPrimaryIndexCapacity = value.MaximumPayloadIndexCapacity = 32;
        value.MaximumResidentBytes = 8192;
        return value;
    }

    private static FakeSession LargeSession(byte value)
        => new(CreateImage(value))
        {
            Capacity = new NativeAppliedOwnershipCapacity
            {
                StructSize = NativeAppliedOwnershipAbi.CapacitySize,
                RecordCapacity = 16, PrimaryIndexCapacity = 32, PayloadIndexCapacity = 32,
                RecordSize = NativeAppliedOwnershipAbi.RecordSize, MaximumImageBytes = 512, ResidentBytes = 8192
            }
        };

    private sealed class RefusingAcquire(IComputeScratch inner) : IComputeScratch
    {
        internal bool Refuse = true;
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => Refuse ? throw new IOException("recorded allocation refusal") : inner.Acquire();
        public void Dispose() => inner.Dispose();
    }

    private static NativeAppliedOwnershipCreateConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.CreateConfigurationSize,
            LedgerInstanceLow = 1,
            RecordCapacity = 1,
            PrimaryIndexCapacity = 1,
            PayloadIndexCapacity = 1,
            MaximumResidentBytes = 4096,
            MaximumImageBytes = 512
        };

    private static NativeAppliedOwnershipOpenConfiguration CreateOpenConfiguration()
        => new()
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.OpenConfigurationSize,
            MaximumRecordCapacity = 1,
            MaximumPrimaryIndexCapacity = 1,
            MaximumPayloadIndexCapacity = 1,
            MaximumResidentBytes = 4096,
            MaximumImageBytes = 512
        };

    private static byte[] CreateImage(byte value)
        => Enumerable.Repeat(value, checked((int)NativeAppliedOwnershipAbi.ImageHeaderSize))
            .ToArray();

    private sealed class FakeFactory(FakeSession session)
        : INativeAppliedOwnershipSessionFactory
    {
        public NativeAppliedOwnershipStatus OpenStatus { get; init; }

        public int CreateCount { get; private set; }

        public int OpenCount { get; private set; }

        public byte[]? LastOpenedImage { get; private set; }

        public INativeAppliedOwnershipSession Create(
            in NativeAppliedOwnershipCreateConfiguration configuration)
        {
            CreateCount++;
            return session;
        }

        public NativeAppliedOwnershipStatus TryOpenExisting(
            in NativeAppliedOwnershipOpenConfiguration configuration,
            ReadOnlySpan<byte> image,
            out INativeAppliedOwnershipSession? openedSession)
        {
            OpenCount++;
            LastOpenedImage = image.ToArray();
            openedSession = OpenStatus == NativeAppliedOwnershipStatus.Ok ? session : null;
            return OpenStatus;
        }
    }

    private sealed class FakeSession(byte[] initialImage) : INativeAppliedOwnershipSession
    {
        private byte[] image = initialImage.ToArray();

        public NativeAppliedOwnershipCapacity Capacity { get; init; } = new()
        {
            StructSize = NativeAppliedOwnershipAbi.CapacitySize,
            RecordCapacity = 1,
            PrimaryIndexCapacity = 1,
            PayloadIndexCapacity = 1,
            RecordSize = NativeAppliedOwnershipAbi.RecordSize,
            MaximumImageBytes = 512,
            ResidentBytes = 4096
        };

        public NativeAppliedOwnershipStatus PromoteStatus { get; set; }
            = NativeAppliedOwnershipStatus.Ok;

        public NativeAppliedOwnershipStatus TransitionStatus { get; set; }
            = NativeAppliedOwnershipStatus.Ok;

        public NativeAppliedOwnershipStatus RemoveStatus { get; set; }
            = NativeAppliedOwnershipStatus.Ok;

        public NativeAppliedOwnershipStatus EncodeStatus { get; set; }
            = NativeAppliedOwnershipStatus.Ok;

        public byte[]? NextMutationImage { get; set; }

        public NativeAppliedOwnershipRecord[] SnapshotRecords { get; set; } = [];

        public bool RequireZeroSnapshotInputs { get; set; }
        public int DisposeCount { get; private set; }

        public NativeAppliedOwnershipPromoteInput LastPromote { get; private set; }

        public NativeAppliedOwnershipTransitionInput LastTransition { get; private set; }

        public NativeAppliedOwnershipRemoveInput LastRemove { get; private set; }

        public NativeAppliedOwnershipStatus Promote(
            in NativeAppliedOwnershipPromoteInput input)
        {
            LastPromote = input;
            return ApplyMutation(PromoteStatus);
        }

        public NativeAppliedOwnershipStatus PlanTransition(
            in NativeAppliedOwnershipPrimaryIdentity primary,
            in NativeAppliedOwnershipOriginalBinding transitionBinding,
            in NativeAppliedOwnershipDurablePayloadReference transitionPayload,
            out NativeAppliedOwnershipTransitionInput transition)
        {
            transition = default;
            return NativeAppliedOwnershipStatus.NoData;
        }

        public NativeAppliedOwnershipStatus Transition(
            in NativeAppliedOwnershipTransitionInput input)
        {
            LastTransition = input;
            return ApplyMutation(TransitionStatus);
        }

        public NativeAppliedOwnershipStatus Remove(in NativeAppliedOwnershipRemoveInput input)
        {
            LastRemove = input;
            return ApplyMutation(RemoveStatus);
        }

        public NativeAppliedOwnershipStatus Get(
            in NativeAppliedOwnershipPrimaryIdentity primary,
            out NativeAppliedOwnershipRecord record)
        {
            record = SnapshotRecords.Length == 0 ? default : SnapshotRecords[0];
            return SnapshotRecords.Length == 0
                ? NativeAppliedOwnershipStatus.NoData
                : NativeAppliedOwnershipStatus.Ok;
        }

        public NativeAppliedOwnershipStatus GetSnapshot(
            ref NativeAppliedOwnershipSnapshotHeader header,
            Span<NativeAppliedOwnershipRecord> records)
        {
            if (RequireZeroSnapshotInputs &&
                (header.AbiVersion != 0 ||
                 header.StructSize != 0 ||
                 ContainsNonZeroRecord(records)))
            {
                return NativeAppliedOwnershipStatus.AbiMismatch;
            }
            SnapshotRecords.CopyTo(records);
            header.LedgerRevision = 1;
            header.LedgerInstanceLow = 1;
            header.EntryCount = checked((uint)SnapshotRecords.Length);
            header.RecordCapacity = Capacity.RecordCapacity;
            header.PrimaryIndexCapacity = Capacity.PrimaryIndexCapacity;
            header.PayloadIndexCapacity = Capacity.PayloadIndexCapacity;
            header.MaximumImageBytes = Capacity.MaximumImageBytes;
            header.ResidentBytes = Capacity.ResidentBytes;
            return NativeAppliedOwnershipStatus.Ok;
        }

        private static bool ContainsNonZeroRecord(
            ReadOnlySpan<NativeAppliedOwnershipRecord> records)
        {
            foreach (ref readonly var record in records)
            {
                if (record.RecordRevision != 0)
                {
                    return true;
                }
            }
            return false;
        }

        public NativeAppliedOwnershipStatus Encode(Span<byte> destination, out ulong written)
        {
            if (EncodeStatus != NativeAppliedOwnershipStatus.Ok)
            {
                written = 0;
                return EncodeStatus;
            }

            image.CopyTo(destination);
            written = checked((ulong)image.Length);
            return NativeAppliedOwnershipStatus.Ok;
        }

        public NativeAppliedOwnershipStatus DecodeReplace(ReadOnlySpan<byte> source)
        {
            image = source.ToArray();
            return NativeAppliedOwnershipStatus.Ok;
        }

        public void Dispose()
        {
            DisposeCount++;
        }

        private NativeAppliedOwnershipStatus ApplyMutation(
            NativeAppliedOwnershipStatus status)
        {
            if (status == NativeAppliedOwnershipStatus.Ok && NextMutationImage is not null)
            {
                image = NextMutationImage.ToArray();
                NextMutationImage = null;
            }
            return status;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"rm-applied-ownership-owner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

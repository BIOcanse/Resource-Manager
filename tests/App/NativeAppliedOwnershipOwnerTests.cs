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
    public async Task EmptyNativeSnapshotUsesCompactStorageAndItsExactImage()
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
        Assert.Null(resource); // Startup's one-use image is ordinary; the owner holds no region.
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        var first = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Empty(first.Records);
        Assert.Null(resource); // A snapshot is copied straight into the reader's array.
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(0UL, ledger.Tick().Released);
        var second = await owner.ReadSnapshotAsync(CancellationToken.None);
        Assert.Equal(first.Header.LedgerInstanceLow, second.Header.LedgerInstanceLow);
        Assert.Equal(first.Header.LedgerRevision, second.Header.LedgerRevision);
        Assert.Null(resource);
        Assert.Empty(first.Records);
        Assert.Empty(second.Records);

        var encodedPath = Path.Combine(directory.Path, "native-encode", "ownership.bin");
        var encodedStore = new NativeAppliedOwnershipFileStore(encodedPath);
        using var pathOwner = encodedStore.AcquireOwnerLease();
        using var encoder = new NativeAppliedOwnershipSession(in create);
        ResourceManagerLedgerOwner.ComputeScratch? encodedResource = null;
        using var encodedWorkspace = new NativeAppliedOwnershipWorkspace(encoder.Capacity,
            length => encodedResource = ledger.CreateScratch(length));
        byte[] encodedImage;
        using (var operation = encodedWorkspace.BeginMutation(encoder, 0))
        {
            Assert.Equal(128UL, encodedResource!.Length); // An empty ledger encodes into a header-only region.
            await encodedStore.PersistAsync(encoder, operation.Image, CancellationToken.None);
            encodedImage = operation.Image.ToArray();
            operation.Complete();
        }
        var actualFile = await File.ReadAllBytesAsync(encodedPath);
        Assert.Equal(actualFile, encodedImage[..actualFile.Length]);
        Assert.Equal(0UL, encodedWorkspace.Length);
        Assert.Throws<ObjectDisposedException>(() => encodedResource!.Acquire()); // Closed with its operation.
    }

    [Theory]
    [InlineData(16U)]
    [InlineData(8192U)]
    public void ActualRowsSizeEachMutationImageAndNothingIsHeldBetweenOperations(uint capacity)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var session = new FakeSession(CreateImage(0x51))
        {
            Capacity = new NativeAppliedOwnershipCapacity
            {
                RecordCapacity = capacity, PrimaryIndexCapacity = capacity, PayloadIndexCapacity = capacity,
                RecordSize = NativeAppliedOwnershipAbi.RecordSize, MaximumImageBytes = 16 * 1024 * 1024,
                ResidentBytes = 16 * 1024 * 1024
            },
            SnapshotRecords = [new NativeAppliedOwnershipRecord { RecordRevision = 71 }]
        };
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativeAppliedOwnershipWorkspace(session.Capacity, bytes =>
        {
            var region = ledger.CreateScratch(bytes);
            regions.Add(region);
            return region;
        });
        Assert.Empty(regions); // The configured capacity sizes nothing.
        var first = workspace.ReadSnapshot(session);
        Assert.Equal(71UL, Assert.Single(first.Records).RecordRevision);
        Assert.Empty(regions); // Native records are the only stored copy; a read builds the reader's array.
        using (var operation = workspace.BeginMutation(session, 1))
        {
            Assert.Equal(768, operation.Image.Length); // Header, one record and one addition.
            Assert.Equal(768UL, workspace.Length);
            operation.Complete();
        }
        Assert.Equal(0UL, workspace.Length);
        session.SnapshotRecords = Enumerable.Range(71, 13)
            .Select(revision => new NativeAppliedOwnershipRecord { RecordRevision = (ulong)revision }).ToArray();
        using (var operation = workspace.BeginMutation(session, 0))
        {
            Assert.Equal(4288, operation.Image.Length); // Thirteen records cross a page, so this region is in Ledger.
            Assert.NotEqual(0UL, ledger.ReadTable().RegisteredBytes);
            operation.Complete();
        }
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(2, regions.Count);
        Assert.All(regions, region => Assert.Throws<ObjectDisposedException>(() => region.Acquire()));
        session.SnapshotRecords[0].RecordRevision = 73;
        var second = workspace.ReadSnapshot(session);
        Assert.Equal(13, second.Records.Count);
        Assert.Equal(73UL, second.Records[0].RecordRevision);
        Assert.Equal(71UL, Assert.Single(first.Records).RecordRevision);
        Assert.Equal(2, regions.Count);
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
            // Reads hold no region, so the refused close is reached through a mutation's image region.
            ComputeScratchLease? foreign = null;
            session.OnMutation = () => foreign = resource!.Acquire();
            var refused = await Assert.ThrowsAsync<LedgerException>(() => owner.PromoteAsync(default, CancellationToken.None));
            Assert.Equal(LedgerStatus.InUse, refused.Status);
            using (var held = foreign!)
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
        var refuse = true; // Each mutation creates its own region, so the refusal is chosen per creation.
        var session = LargeSession(0x71);
        var path = Path.Combine(directory.Path, "ownership.bin");
        await using var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(path), new FakeFactory(session),
            LargeConfiguration(), LargeOpenConfiguration(),
            length => resource = new RefusingAcquire(ledger.CreateScratch(length)) { Refuse = refuse }, CancellationToken.None);
        session.NextMutationImage = CreateImage(0x72);
        await Assert.ThrowsAsync<IOException>(() => owner.PromoteAsync(default, CancellationToken.None));
        Assert.NotNull(session.NextMutationImage);
        Assert.Equal(NativeAppliedOwnershipOwnerState.Ready, owner.State);
        Assert.Null(owner.PersistenceFailure);
        Assert.Equal(CreateImage(0x71), await File.ReadAllBytesAsync(path));
        Assert.NotNull(resource);
        refuse = false;
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
            // Reads hold no region; a mutation's image lease is the use whose end fails.
            await Assert.ThrowsAsync<IOException>(() => owner.PromoteAsync(default, CancellationToken.None));
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
            // Reads hold no region, so the refused close is reached through a mutation's image region.
            ComputeScratchLease? foreign = null;
            session.OnMutation = () => foreign = resource!.Acquire();
            await Assert.ThrowsAsync<LedgerException>(() => owner.PromoteAsync(default, CancellationToken.None));
            using (var held = foreign!)
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

    [Fact]
    public async Task RealNativeRecordsAreTheOnlyStoredCopyAndRestartRestoresThemUnchanged()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = new ResourceManagerLedgerOwner();
        var path = Path.Combine(directory.Path, "ownership.bin");
        var create = LargeConfiguration();
        create.MaximumImageBytes = 16384;
        create.MaximumResidentBytes = 65536;
        var open = LargeOpenConfiguration();
        open.MaximumImageBytes = create.MaximumImageBytes;
        open.MaximumResidentBytes = create.MaximumResidentBytes;
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        IComputeScratch CreateRegion(ulong length)
        {
            var region = ledger.CreateScratch(length);
            regions.Add(region);
            return region;
        }

        NativeAppliedOwnershipSnapshot before;
        await using (var owner = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(path), new NativeAppliedOwnershipSessionFactory(),
            create, open, CreateRegion, CancellationToken.None))
        {
            for (uint id = 1; id <= 13; id++)
                Assert.Equal(NativeAppliedOwnershipStatus.Ok, await owner.PromoteAsync(RealPromote(id), CancellationToken.None));
            before = await owner.ReadSnapshotAsync(CancellationToken.None);
            Assert.Equal(Enumerable.Range(1, 13).Select(id => (ulong)id),
                before.Records.Select(record => record.Primary.TargetId).Order());
            // One image region per mutation, sized for that mutation's rows and closed with it.
            Assert.Equal(Enumerable.Range(1, 13).Select(rows => (ulong)NativeAppliedOwnershipWorkspace.ImageLengthForRows((uint)rows)),
                regions.Select(region => region.Length));
            Assert.All(regions, region => Assert.Throws<ObjectDisposedException>(() => region.Acquire()));
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        }

        var persisted = await File.ReadAllBytesAsync(path);
        Assert.Equal(NativeAppliedOwnershipWorkspace.ImageLengthForRows(13), persisted.Length);
        await using var restarted = await NativeAppliedOwnershipOwner.OpenOrCreateAsync(
            new NativeAppliedOwnershipFileStore(path), new NativeAppliedOwnershipSessionFactory(),
            create, open, CreateRegion, CancellationToken.None);
        var after = await restarted.ReadSnapshotAsync(CancellationToken.None);
        Assert.Equal(before.Header.LedgerRevision, after.Header.LedgerRevision);
        Assert.Equal(before.Header.LedgerInstanceLow, after.Header.LedgerInstanceLow);
        Assert.Equal(before.Header.EntryCount, after.Header.EntryCount);
        Assert.Equal(RecordBytes(before), RecordBytes(after));
        Assert.Equal(persisted, await File.ReadAllBytesAsync(path)); // Restore reads; it does not rewrite.
        Assert.Equal(13, regions.Count); // Restore and reads create no region.

        static byte[] RecordBytes(NativeAppliedOwnershipSnapshot snapshot)
            => System.Runtime.InteropServices.MemoryMarshal.AsBytes(snapshot.Records.ToArray().AsSpan()).ToArray();
    }

    private static NativeAppliedOwnershipPromoteInput RealPromote(uint id) => new()
    {
        AbiVersion = NativeAppliedOwnershipAbi.Version, StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
        ExpectedLedgerRevision = id,
        Primary = new() { Scope = 1, TargetId = id, SoftwareId = id, ProcessStartKey = id, ProcessId = id },
        OriginalBinding = new()
        {
            JournalInstanceLow = 11, JournalInstanceHigh = 12,
            ActionIdentity = new()
            {
                ConfigurationGeneration = 1, PlanEpoch = 2, ActionId = id, HostSessionIncarnation = 4,
                TargetId = id, SoftwareId = id, ProcessStartKey = id, ProcessId = id
            },
            Scope = 1, Disposition = 1, DomainMask = 1, GradeValidMask = 1,
            ProcessFromGrade = 0, ProcessToGrade = -1, MaximumRecoveryAttempts = 3,
            RecoveryDeadlineUtcMilliseconds = 11000
        },
        Payload = new() { Slot = id, Generation = 1, Length = 512, DigestLow = 1, DigestHigh = 2 },
        CurrentGrades = new() { ValidMask = 1, ProcessGrade = -1 }, PromotedAtUtcMilliseconds = 1000
    };

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
            header.LedgerRevision = 1;
            header.LedgerInstanceLow = 1;
            header.EntryCount = checked((uint)SnapshotRecords.Length);
            header.RecordCapacity = Capacity.RecordCapacity;
            header.PrimaryIndexCapacity = Capacity.PrimaryIndexCapacity;
            header.PayloadIndexCapacity = Capacity.PayloadIndexCapacity;
            header.MaximumImageBytes = Capacity.MaximumImageBytes;
            header.ResidentBytes = Capacity.ResidentBytes;
            if (records.Length < SnapshotRecords.Length) return NativeAppliedOwnershipStatus.BufferTooSmall;
            SnapshotRecords.CopyTo(records);
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

        // Runs inside the owner's mutation, while that mutation's image region is leased.
        public Action? OnMutation { get; set; }

        private NativeAppliedOwnershipStatus ApplyMutation(
            NativeAppliedOwnershipStatus status)
        {
            OnMutation?.Invoke();
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

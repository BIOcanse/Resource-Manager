using ResourceManager.App.Infrastructure.NativeCore;

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

        public NativeAppliedOwnershipCapacity Capacity { get; } = new()
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

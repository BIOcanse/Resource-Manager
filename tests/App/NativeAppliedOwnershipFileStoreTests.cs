using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeAppliedOwnershipFileStoreTests
{
    [Fact]
    public void ConstructorRequiresAbsolutePath()
    {
        Assert.Throws<ArgumentException>(
            () => new NativeAppliedOwnershipFileStore("relative/ownership.bin"));
    }

    [Fact]
    public void OwnerLeaseIsExclusiveUntilReleased()
    {
        using var directory = new TemporaryDirectory();
        var store = new NativeAppliedOwnershipFileStore(
            Path.Combine(directory.Path, "ownership.bin"));

        using (store.AcquireOwnerLease())
        {
            Assert.Throws<NativeAppliedOwnershipOwnerLeaseException>(
                () => store.AcquireOwnerLease());
        }

        using var reacquired = store.AcquireOwnerLease();
    }

    [Fact]
    public async Task MissingImageReturnsNoDataWithoutOpeningNativeSession()
    {
        using var directory = new TemporaryDirectory();
        var store = new NativeAppliedOwnershipFileStore(
            Path.Combine(directory.Path, "ownership.bin"));
        var factory = new RecordingFactory();

        var result = await store.TryOpenAsync(
            factory,
            CreateOpenConfiguration(),
            CancellationToken.None);

        Assert.Equal(NativeAppliedOwnershipStatus.NoData, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ExistingImageIsPassedWholeToNativeDecoder()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ownership.bin");
        var image = Enumerable.Range(0, 192).Select(static value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(path, image);
        var session = new FakeSession(image);
        var factory = new RecordingFactory
        {
            OpenStatus = NativeAppliedOwnershipStatus.Ok,
            OpenSession = session
        };
        var store = new NativeAppliedOwnershipFileStore(path);

        var result = await store.TryOpenAsync(
            factory,
            CreateOpenConfiguration(),
            CancellationToken.None);

        Assert.Equal(NativeAppliedOwnershipStatus.Ok, result.Status);
        Assert.Same(session, result.Session);
        Assert.Equal(image, factory.LastOpenedImage);
    }

    [Fact]
    public async Task PersistCommitsExactCanonicalImage()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ownership.bin");
        var image = Enumerable.Range(0, 224).Select(static value => (byte)(value * 7)).ToArray();
        var session = new FakeSession(image);
        var store = new NativeAppliedOwnershipFileStore(path);
        var workspace = new NativeAppliedOwnershipWorkspace(session.Capacity);

        await store.PersistAsync(session, workspace, CancellationToken.None);

        Assert.Equal(image, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private static NativeAppliedOwnershipOpenConfiguration CreateOpenConfiguration()
        => new()
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.OpenConfigurationSize,
            MaximumRecordCapacity = 8,
            MaximumPrimaryIndexCapacity = 8,
            MaximumPayloadIndexCapacity = 8,
            MaximumResidentBytes = 1 << 20,
            MaximumImageBytes = 4096
        };

    private sealed class RecordingFactory : INativeAppliedOwnershipSessionFactory
    {
        public NativeAppliedOwnershipStatus OpenStatus { get; init; }

        public INativeAppliedOwnershipSession? OpenSession { get; init; }

        public int OpenCount { get; private set; }

        public byte[]? LastOpenedImage { get; private set; }

        public INativeAppliedOwnershipSession Create(
            in NativeAppliedOwnershipCreateConfiguration configuration)
            => throw new NotSupportedException();

        public NativeAppliedOwnershipStatus TryOpenExisting(
            in NativeAppliedOwnershipOpenConfiguration configuration,
            ReadOnlySpan<byte> image,
            out INativeAppliedOwnershipSession? session)
        {
            OpenCount++;
            LastOpenedImage = image.ToArray();
            session = OpenSession;
            return OpenStatus;
        }
    }

    private sealed class FakeSession(byte[] image) : INativeAppliedOwnershipSession
    {
        private readonly byte[] image = image.ToArray();

        public NativeAppliedOwnershipCapacity Capacity { get; } = new()
        {
            StructSize = NativeAppliedOwnershipAbi.CapacitySize,
            RecordCapacity = 1,
            PrimaryIndexCapacity = 1,
            PayloadIndexCapacity = 1,
            RecordSize = NativeAppliedOwnershipAbi.RecordSize,
            MaximumImageBytes = checked((ulong)image.Length),
            ResidentBytes = 1
        };

        public NativeAppliedOwnershipStatus Promote(
            in NativeAppliedOwnershipPromoteInput input)
            => throw new NotSupportedException();

        public NativeAppliedOwnershipStatus PlanTransition(
            in NativeAppliedOwnershipPrimaryIdentity primary,
            in NativeAppliedOwnershipOriginalBinding transitionBinding,
            in NativeAppliedOwnershipDurablePayloadReference transitionPayload,
            out NativeAppliedOwnershipTransitionInput transition)
        {
            transition = default;
            throw new NotSupportedException();
        }

        public NativeAppliedOwnershipStatus Transition(
            in NativeAppliedOwnershipTransitionInput input)
            => throw new NotSupportedException();

        public NativeAppliedOwnershipStatus Remove(in NativeAppliedOwnershipRemoveInput input)
            => throw new NotSupportedException();

        public NativeAppliedOwnershipStatus Get(
            in NativeAppliedOwnershipPrimaryIdentity primary,
            out NativeAppliedOwnershipRecord record)
            => throw new NotSupportedException();

        public NativeAppliedOwnershipStatus GetSnapshot(
            ref NativeAppliedOwnershipSnapshotHeader header,
            Span<NativeAppliedOwnershipRecord> records)
            => throw new NotSupportedException();

        public NativeAppliedOwnershipStatus Encode(Span<byte> destination, out ulong written)
        {
            image.CopyTo(destination);
            written = checked((ulong)image.Length);
            return NativeAppliedOwnershipStatus.Ok;
        }

        public NativeAppliedOwnershipStatus DecodeReplace(ReadOnlySpan<byte> source)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"rm-applied-ownership-{Guid.NewGuid():N}");
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

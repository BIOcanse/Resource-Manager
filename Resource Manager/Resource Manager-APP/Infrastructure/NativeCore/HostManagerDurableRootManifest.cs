using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal interface IHostManagerDurableRootManifestCommitter
{
    void Commit(
        string temporaryPath,
        string manifestPath,
        ReadOnlySpan<byte> expectedImage);
}

internal sealed class WindowsHostManagerDurableRootManifestCommitter
    : IHostManagerDurableRootManifestCommitter
{
    internal static WindowsHostManagerDurableRootManifestCommitter Instance { get; } = new();

    private WindowsHostManagerDurableRootManifestCommitter()
    {
    }

    public void Commit(
        string temporaryPath,
        string manifestPath,
        ReadOnlySpan<byte> expectedImage)
    {
        WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, manifestPath);
        using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length != expectedImage.Length)
        {
            throw new IOException(
                "The Host Manager durable root manifest commit has an unexpected length.");
        }

        var readback = GC.AllocateUninitializedArray<byte>(expectedImage.Length);
        stream.ReadExactly(readback);
        if (!readback.AsSpan().SequenceEqual(expectedImage))
        {
            throw new IOException(
                "The Host Manager durable root manifest commit was not verified.");
        }
    }
}

internal sealed class HostManagerDurableRootManifest
{
    private const int LegacyManifestSize = 96;
    private const uint LegacyVersion = 1;
    private const int ManifestSize = 144;
    private const uint Version = 2;
    private const uint StateInitializing = 1;
    private const uint StateInitialized = 2;
    private const uint StatePending = 3;
    private static ReadOnlySpan<byte> LegacyMagic => "RMROOT01"u8;
    private static ReadOnlySpan<byte> Magic => "RMROOT02"u8;

    private readonly string canonicalPath;
    private readonly IHostManagerDurableRootManifestCommitter committer;
    private readonly byte[] storeKindSha256;
    private readonly string manifestPath;

    internal HostManagerDurableRootManifest(
        string canonicalPath,
        string storeKind)
        : this(
            canonicalPath,
            storeKind,
            WindowsHostManagerDurableRootManifestCommitter.Instance)
    {
    }

    internal HostManagerDurableRootManifest(
        string canonicalPath,
        string storeKind,
        IHostManagerDurableRootManifestCommitter committer)
        : this(
            canonicalPath,
            ResolveAdjacentManifestPath(canonicalPath),
            storeKind,
            committer)
    {
    }

    internal HostManagerDurableRootManifest(
        string canonicalPath,
        string manifestPath,
        string storeKind,
        IHostManagerDurableRootManifestCommitter committer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKind);
        this.canonicalPath = Path.GetFullPath(canonicalPath);
        this.manifestPath = Path.GetFullPath(manifestPath);
        storeKindSha256 = SHA256.HashData(Encoding.UTF8.GetBytes(storeKind));
        this.committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    internal void RequireVirginOrInitializing()
    {
        var manifest = Read();
        if (manifest is null || manifest.State == StateInitializing)
        {
            return;
        }

        if (manifest.State == StatePending
            && IsZeroDigest(manifest.CanonicalSha256)
            && !File.Exists(canonicalPath))
        {
            Write(ManifestState.Initializing(manifest.RootIncarnation));
            return;
        }

        throw new IOException(
            "The initialized Host Manager durable root lost its canonical file.");
    }

    internal void EnsureInitializing()
    {
        var manifest = Read();
        if (manifest is null)
        {
            Write(ManifestState.Initializing(Guid.NewGuid()));
            return;
        }

        if (manifest.State == StatePending)
        {
            throw new InvalidOperationException(
                "The Host Manager durable root has an unresolved canonical transition.");
        }
    }

    internal HostManagerDurableRootTransition BeginCanonicalTransition(
        ReadOnlySpan<byte> canonicalImage)
    {
        var manifest = Read()
            ?? throw new InvalidOperationException(
                "The Host Manager durable root manifest is not initialized.");
        if (manifest.State == StatePending)
        {
            throw new InvalidOperationException(
                "The Host Manager durable root already has a pending canonical transition.");
        }

        var priorCanonicalSha256 = manifest.State == StateInitialized
            ? manifest.CanonicalSha256.ToArray()
            : new byte[SHA256.HashSizeInBytes];
        var transition = new HostManagerDurableRootTransition(
            manifest.RootIncarnation,
            Guid.NewGuid(),
            priorCanonicalSha256,
            SHA256.HashData(canonicalImage));
        Write(ManifestState.Pending(
            manifest.RootIncarnation,
            transition.TransactionId,
            transition.PriorCanonicalSha256,
            transition.NextCanonicalSha256));
        return transition;
    }

    internal void FinalizeCanonicalTransition(
        HostManagerDurableRootTransition transition)
    {
        var manifest = Read()
            ?? throw new InvalidDataException(
                "The Host Manager durable root manifest disappeared during commit.");
        if (manifest.RootIncarnation != transition.RootIncarnation)
        {
            throw new InvalidDataException(
                "The Host Manager durable root incarnation changed during commit.");
        }

        if (manifest.State == StateInitialized
            && DigestsEqual(
                manifest.CanonicalSha256,
                transition.NextCanonicalSha256))
        {
            return;
        }

        if (manifest.State != StatePending
            || manifest.TransactionId != transition.TransactionId
            || !DigestsEqual(
                manifest.CanonicalSha256,
                transition.PriorCanonicalSha256)
            || !DigestsEqual(
                manifest.PendingCanonicalSha256,
                transition.NextCanonicalSha256))
        {
            throw new InvalidDataException(
                "The Host Manager durable root pending transition changed during commit.");
        }

        Write(ManifestState.Initialized(
            manifest.RootIncarnation,
            transition.NextCanonicalSha256));
    }

    internal void ValidateOrAdoptCanonical(ReadOnlySpan<byte> canonicalImage)
        => ValidateCanonical(canonicalImage, allowAdoption: true);

    internal void ValidateExistingCanonical(ReadOnlySpan<byte> canonicalImage)
        => ValidateCanonical(canonicalImage, allowAdoption: false);

    private void ValidateCanonical(
        ReadOnlySpan<byte> canonicalImage,
        bool allowAdoption)
    {
        var canonicalSha256 = SHA256.HashData(canonicalImage);
        var manifest = Read();
        if (manifest is null || manifest.State == StateInitializing)
        {
            if (!allowAdoption)
            {
                throw new InvalidDataException(
                    "The Host Manager durable root is not initialized for its canonical file.");
            }

            Write(ManifestState.Initialized(
                manifest?.RootIncarnation ?? Guid.NewGuid(),
                canonicalSha256));
            return;
        }

        if (manifest.State == StateInitialized)
        {
            if (DigestsEqual(manifest.CanonicalSha256, canonicalSha256))
            {
                return;
            }

            throw new InvalidDataException(
                "The Host Manager durable root manifest does not match the canonical file.");
        }

        if (DigestsEqual(manifest.PendingCanonicalSha256, canonicalSha256))
        {
            Write(ManifestState.Initialized(
                manifest.RootIncarnation,
                manifest.PendingCanonicalSha256));
            return;
        }

        if (!IsZeroDigest(manifest.CanonicalSha256)
            && DigestsEqual(manifest.CanonicalSha256, canonicalSha256))
        {
            Write(ManifestState.Initialized(
                manifest.RootIncarnation,
                manifest.CanonicalSha256));
            return;
        }

        throw new InvalidDataException(
            "The Host Manager durable root pending transition does not match the canonical file.");
    }

    internal HostManagerDurableRootIdentity CaptureInitializedIdentity()
    {
        var manifest = Read(out var manifestImage)
            ?? throw new InvalidDataException(
                "The Host Manager durable root manifest is missing.");
        if (manifest.State != StateInitialized || manifestImage is null)
        {
            throw new InvalidDataException(
                "The Host Manager durable root is not initialized.");
        }

        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length <= 0)
        {
            throw new InvalidDataException(
                "The Host Manager durable root canonical file is empty.");
        }
        var canonicalSha256 = SHA256.HashData(stream);
        if (!DigestsEqual(manifest.CanonicalSha256, canonicalSha256))
        {
            throw new InvalidDataException(
                "The Host Manager durable root manifest does not match the canonical file.");
        }

        return new HostManagerDurableRootIdentity(
            manifest.RootIncarnation,
            stream.Length,
            canonicalSha256,
            manifestImage.Length,
            SHA256.HashData(manifestImage));
    }

    private ManifestState? Read()
        => Read(out _);

    private ManifestState? Read(out byte[]? image)
    {
        image = null;
        FileStream stream;
        try
        {
            stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new IOException(
                "The Host Manager durable root directory is unavailable.",
                exception);
        }

        using (stream)
        {
            if (stream.Length is not ManifestSize and not LegacyManifestSize)
            {
                throw new InvalidDataException(
                    "The Host Manager durable root manifest has an invalid size.");
            }

            image = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(image);
        }

        if (image.Length == LegacyManifestSize)
        {
            var legacyState = DecodeLegacy(image);
            image = Write(legacyState);
            return legacyState;
        }

        return DecodeCurrent(image);
    }

    private ManifestState DecodeCurrent(ReadOnlySpan<byte> image)
    {
        if (!image[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(8, 4)) != Version
            || !image.Slice(16, SHA256.HashSizeInBytes)
                .SequenceEqual(storeKindSha256))
        {
            throw new InvalidDataException(
                "The Host Manager durable root manifest is invalid.");
        }

        var state = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(12, 4));
        var rootIncarnation = new Guid(image.Slice(48, 16));
        var canonicalSha256 = image.Slice(64, SHA256.HashSizeInBytes).ToArray();
        var pendingCanonicalSha256 = image.Slice(96, SHA256.HashSizeInBytes).ToArray();
        var transactionId = new Guid(image.Slice(128, 16));
        var validState = state switch
        {
            StateInitializing => IsZeroDigest(canonicalSha256)
                && IsZeroDigest(pendingCanonicalSha256)
                && transactionId == Guid.Empty,
            StateInitialized => !IsZeroDigest(canonicalSha256)
                && IsZeroDigest(pendingCanonicalSha256)
                && transactionId == Guid.Empty,
            StatePending => !IsZeroDigest(pendingCanonicalSha256)
                && transactionId != Guid.Empty,
            _ => false
        };
        if (rootIncarnation == Guid.Empty || !validState)
        {
            throw new InvalidDataException(
                "The Host Manager durable root manifest state is invalid.");
        }

        return new ManifestState(
            state,
            rootIncarnation,
            transactionId,
            canonicalSha256,
            pendingCanonicalSha256);
    }

    private ManifestState DecodeLegacy(ReadOnlySpan<byte> image)
    {
        if (!image[..LegacyMagic.Length].SequenceEqual(LegacyMagic)
            || BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(8, 4))
                != LegacyVersion
            || !image.Slice(16, SHA256.HashSizeInBytes)
                .SequenceEqual(storeKindSha256))
        {
            throw new InvalidDataException(
                "The legacy Host Manager durable root manifest is invalid.");
        }

        var state = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(12, 4));
        var rootIncarnation = new Guid(image.Slice(48, 16));
        var canonicalSha256 = image.Slice(64, SHA256.HashSizeInBytes).ToArray();
        var validState = state switch
        {
            StateInitializing => IsZeroDigest(canonicalSha256),
            StateInitialized => !IsZeroDigest(canonicalSha256),
            _ => false
        };
        if (rootIncarnation == Guid.Empty || !validState)
        {
            throw new InvalidDataException(
                "The legacy Host Manager durable root manifest state is invalid.");
        }

        if (state == StateInitialized)
        {
            ValidateLegacyCanonical(canonicalSha256);
        }

        return new ManifestState(
            state,
            rootIncarnation,
            Guid.Empty,
            canonicalSha256,
            new byte[SHA256.HashSizeInBytes]);
    }

    private void ValidateLegacyCanonical(ReadOnlySpan<byte> expectedSha256)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException(
                "The initialized legacy Host Manager durable root lost its canonical file.",
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new InvalidDataException(
                "The initialized legacy Host Manager durable root lost its canonical file.",
                exception);
        }

        using (stream)
        {
            if (stream.Length <= 0
                || !DigestsEqual(expectedSha256, SHA256.HashData(stream)))
            {
                throw new InvalidDataException(
                    "The legacy Host Manager durable root manifest does not match the canonical file.");
            }
        }
    }

    private byte[] Write(ManifestState state)
    {
        var directory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException(
                "The Host Manager durable root manifest path has no parent directory.");
        Directory.CreateDirectory(directory);
        var image = new byte[ManifestSize];
        Magic.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), state.State);
        storeKindSha256.CopyTo(image, 16);
        state.RootIncarnation.TryWriteBytes(image.AsSpan(48, 16));
        state.CanonicalSha256.CopyTo(image, 64);
        state.PendingCanonicalSha256.CopyTo(image, 96);
        state.TransactionId.TryWriteBytes(image.AsSpan(128, 16));

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(manifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(image);
                stream.Flush(flushToDisk: true);
            }

            committer.Commit(temporaryPath, manifestPath, image);
            return image;
        }
        finally
        {
            _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath);
        }
    }

    private static bool DigestsEqual(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
        => left.Length == SHA256.HashSizeInBytes
            && right.Length == SHA256.HashSizeInBytes
            && CryptographicOperations.FixedTimeEquals(left, right);

    private static bool IsZeroDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        foreach (var value in digest)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static string ResolveAdjacentManifestPath(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        return $"{Path.GetFullPath(canonicalPath)}.root.manifest";
    }

    private sealed record ManifestState(
        uint State,
        Guid RootIncarnation,
        Guid TransactionId,
        byte[] CanonicalSha256,
        byte[] PendingCanonicalSha256)
    {
        internal static ManifestState Initializing(Guid rootIncarnation)
            => new(
                StateInitializing,
                rootIncarnation,
                Guid.Empty,
                new byte[SHA256.HashSizeInBytes],
                new byte[SHA256.HashSizeInBytes]);

        internal static ManifestState Initialized(
            Guid rootIncarnation,
            ReadOnlySpan<byte> canonicalSha256)
            => new(
                StateInitialized,
                rootIncarnation,
                Guid.Empty,
                canonicalSha256.ToArray(),
                new byte[SHA256.HashSizeInBytes]);

        internal static ManifestState Pending(
            Guid rootIncarnation,
            Guid transactionId,
            ReadOnlySpan<byte> priorCanonicalSha256,
            ReadOnlySpan<byte> nextCanonicalSha256)
            => new(
                StatePending,
                rootIncarnation,
                transactionId,
                priorCanonicalSha256.ToArray(),
                nextCanonicalSha256.ToArray());
    }
}

internal readonly record struct HostManagerDurableRootTransition(
    Guid RootIncarnation,
    Guid TransactionId,
    byte[] PriorCanonicalSha256,
    byte[] NextCanonicalSha256);

internal readonly record struct HostManagerDurableRootIdentity(
    Guid RootIncarnation,
    long CanonicalLength,
    byte[] CanonicalSha256,
    long ManifestLength,
    byte[] ManifestSha256);

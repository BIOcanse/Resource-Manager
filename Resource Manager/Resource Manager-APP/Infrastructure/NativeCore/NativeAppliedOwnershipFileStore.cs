using System.Buffers;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeAppliedOwnershipFileStore
{
    private const int IoBufferSize = 64 * 1024;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HostManagerDurableRootManifest rootManifest;
    private readonly string ledgerPath;
    private readonly string ownerLeasePath;

    public NativeAppliedOwnershipFileStore(string ledgerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        if (!Path.IsPathFullyQualified(ledgerPath))
        {
            throw new ArgumentException(
                "The applied ownership ledger path must be absolute.",
                nameof(ledgerPath));
        }

        this.ledgerPath = Path.GetFullPath(ledgerPath);
        rootManifest = new HostManagerDurableRootManifest(
            this.ledgerPath,
            "applied-ownership");
        ownerLeasePath = $"{this.ledgerPath}.owner.lock";
    }

    public NativeAppliedOwnershipPathLease AcquireOwnerLease()
    {
        var directory = Path.GetDirectoryName(ledgerPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The applied ownership ledger path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        try
        {
            var stream = new FileStream(
                ownerLeasePath,
                new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough
                });
            return new NativeAppliedOwnershipPathLease(stream);
        }
        catch (IOException exception)
        {
            throw new NativeAppliedOwnershipOwnerLeaseException(ownerLeasePath, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new NativeAppliedOwnershipOwnerLeaseException(ownerLeasePath, exception);
        }
    }

    public async Task<NativeAppliedOwnershipOpenResult> TryOpenAsync(
        INativeAppliedOwnershipSessionFactory sessionFactory,
        NativeAppliedOwnershipOpenConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);

        await gate.WaitAsync(cancellationToken);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    ledgerPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        BufferSize = IoBufferSize,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });
            }
            catch (FileNotFoundException)
            {
                rootManifest.RequireVirginOrInitializing();
                return new NativeAppliedOwnershipOpenResult(
                    NativeAppliedOwnershipStatus.NoData,
                    null);
            }
            catch (DirectoryNotFoundException)
            {
                rootManifest.RequireVirginOrInitializing();
                return new NativeAppliedOwnershipOpenResult(
                    NativeAppliedOwnershipStatus.NoData,
                    null);
            }

            await using (stream)
            {
                var fileLength = stream.Length;
                if (fileLength < checked((long)NativeAppliedOwnershipAbi.ImageHeaderSize) ||
                    (ulong)fileLength > configuration.MaximumImageBytes ||
                    fileLength > int.MaxValue)
                {
                    return new NativeAppliedOwnershipOpenResult(
                        NativeAppliedOwnershipStatus.CorruptImage,
                        null);
                }

                var image = GC.AllocateUninitializedArray<byte>(checked((int)fileLength));
                await stream.ReadExactlyAsync(image, cancellationToken);
                var status = sessionFactory.TryOpenExisting(
                    in configuration,
                    image,
                    out var session);
                if (status == NativeAppliedOwnershipStatus.Ok)
                {
                    rootManifest.ValidateOrAdoptCanonical(image);
                }
                return new NativeAppliedOwnershipOpenResult(status, session);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PersistAsync(
        INativeAppliedOwnershipSession session,
        NativeAppliedOwnershipWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspace);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var status = session.Encode(workspace.Image, out var written);
            if (status != NativeAppliedOwnershipStatus.Ok ||
                written < NativeAppliedOwnershipAbi.ImageHeaderSize ||
                written > (ulong)workspace.Image.Length)
            {
                throw new InvalidOperationException(
                    $"Native applied ownership encode failed with {status} and length {written}.");
            }

            var directory = Path.GetDirectoryName(ledgerPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The applied ownership ledger path must have a parent directory.");
            }

            Directory.CreateDirectory(directory);
            rootManifest.EnsureInitializing();
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(ledgerPath)}.{Guid.NewGuid():N}.tmp");
            var committed = false;
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = IoBufferSize,
                        Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                    }))
                {
                    await stream.WriteAsync(
                        workspace.Image.AsMemory(0, checked((int)written)),
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                var transition = rootManifest.BeginCanonicalTransition(
                    workspace.Image.AsSpan(0, checked((int)written)));
                WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, ledgerPath);
                await VerifyCommittedImageAsync(
                    workspace.Image.AsMemory(0, checked((int)written)),
                    cancellationToken);
                committed = true;
                rootManifest.FinalizeCanonicalTransition(transition);
            }
            finally
            {
                if (!committed)
                {
                    TryDeleteTemporaryFile(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task VerifyCommittedImageAsync(
        ReadOnlyMemory<byte> expectedImage,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            ledgerPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = IoBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        if (stream.Length != expectedImage.Length)
        {
            throw new InvalidDataException(
                "The committed applied ownership image length is not canonical.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            var offset = 0;
            while (offset < expectedImage.Length)
            {
                var length = Math.Min(buffer.Length, expectedImage.Length - offset);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, length),
                    cancellationToken);
                if (read == 0 ||
                    !buffer.AsSpan(0, read).SequenceEqual(
                        expectedImage.Span.Slice(offset, read)))
                {
                    throw new InvalidDataException(
                        "The committed applied ownership image differs from the native canonical image.");
                }
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

internal sealed class NativeAppliedOwnershipPathLease(FileStream stream) : IDisposable
{
    private FileStream? stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public void Dispose()
        => Interlocked.Exchange(ref stream, null)?.Dispose();
}

internal sealed class NativeAppliedOwnershipOwnerLeaseException(
    string leasePath,
    Exception innerException)
    : IOException(
        $"The applied ownership owner lease is already held or unavailable: {leasePath}",
        innerException);

internal sealed record NativeAppliedOwnershipOpenResult(
    NativeAppliedOwnershipStatus Status,
    INativeAppliedOwnershipSession? Session);

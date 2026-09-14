namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeTransactionJournalFileStore
{
    private const int IoBufferSize = 64 * 1024;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly INativeTransactionJournalCommitHook commitHook;
    private readonly INativeTransactionJournalFileCommitter committer;
    private readonly HostManagerDurableRootManifest rootManifest;
    private readonly string journalPath;
    private readonly string ownerLeasePath;

    internal NativeTransactionJournalFileStore(string journalPath)
        : this(
            journalPath,
            WindowsNativeTransactionJournalFileCommitter.Instance,
            NativeTransactionJournalCommitHook.None)
    {
    }

    internal NativeTransactionJournalFileStore(
        string journalPath,
        INativeTransactionJournalCommitHook commitHook)
        : this(
            journalPath,
            WindowsNativeTransactionJournalFileCommitter.Instance,
            commitHook)
    {
    }

    internal NativeTransactionJournalFileStore(
        string journalPath,
        INativeTransactionJournalFileCommitter committer,
        INativeTransactionJournalCommitHook commitHook)
        : this(
            journalPath,
            committer,
            commitHook,
            WindowsHostManagerDurableRootManifestCommitter.Instance)
    {
    }

    internal NativeTransactionJournalFileStore(
        string journalPath,
        INativeTransactionJournalFileCommitter committer,
        INativeTransactionJournalCommitHook commitHook,
        IHostManagerDurableRootManifestCommitter manifestCommitter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!Path.IsPathFullyQualified(journalPath))
        {
            throw new ArgumentException(
                "The transaction journal path must be absolute.",
                nameof(journalPath));
        }

        this.journalPath = Path.GetFullPath(journalPath);
        rootManifest = new HostManagerDurableRootManifest(
            this.journalPath,
            "transaction-journal",
            manifestCommitter);
        ownerLeasePath = $"{this.journalPath}.owner.lock";
        this.committer = committer ?? throw new ArgumentNullException(nameof(committer));
        this.commitHook = commitHook ?? throw new ArgumentNullException(nameof(commitHook));
    }

    public NativeTransactionJournalPathLease AcquireOwnerLease()
    {
        var directory = Path.GetDirectoryName(journalPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The transaction journal path must have a parent directory.");
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
            return new NativeTransactionJournalPathLease(stream);
        }
        catch (IOException exception)
        {
            throw new NativeTransactionJournalOwnerLeaseException(ownerLeasePath, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new NativeTransactionJournalOwnerLeaseException(ownerLeasePath, exception);
        }
    }

    public async Task<NativeTransactionJournalOpenResult> TryOpenAsync(
        NativeTransactionJournalOpenConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    journalPath,
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
                return new NativeTransactionJournalOpenResult(
                    NativeTransactionJournalStatus.NoData,
                    null);
            }
            catch (DirectoryNotFoundException)
            {
                rootManifest.RequireVirginOrInitializing();
                return new NativeTransactionJournalOpenResult(
                    NativeTransactionJournalStatus.NoData,
                    null);
            }

            await using (stream)
            {
                var maximumImageLength = checked(
                    NativeTransactionJournalAbi.ImageHeaderSize +
                    (ulong)configuration.MaximumRecordCapacity *
                    NativeTransactionJournalSession.SizeOf<NativeTransactionJournalRecord>());
                var fileLength = stream.Length;
                if (fileLength < checked((long)NativeTransactionJournalAbi.ImageHeaderSize) ||
                    (ulong)fileLength > maximumImageLength ||
                    fileLength > int.MaxValue)
                {
                    return new NativeTransactionJournalOpenResult(
                        NativeTransactionJournalStatus.CorruptImage,
                        null);
                }

                var image = GC.AllocateUninitializedArray<byte>(checked((int)fileLength));
                await stream.ReadExactlyAsync(image, cancellationToken);

                var status = NativeTransactionJournalSession.TryOpenExisting(
                    in configuration,
                    image,
                    out var session);
                if (status == NativeTransactionJournalStatus.Ok)
                {
                    rootManifest.ValidateOrAdoptCanonical(image);
                }
                return new NativeTransactionJournalOpenResult(status, session);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PersistAsync(
        NativeTransactionJournalSession session,
        NativeTransactionJournalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspace);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var status = session.Encode(workspace.Image, out var written);
            if (status != NativeTransactionJournalStatus.Ok ||
                written < NativeTransactionJournalAbi.ImageHeaderSize ||
                written > (ulong)workspace.Image.Length)
            {
                throw new InvalidOperationException(
                    $"Native transaction journal encode failed with {status} and length {written}.");
            }

            var directory = Path.GetDirectoryName(journalPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The transaction journal path must have a parent directory.");
            }

            Directory.CreateDirectory(directory);
            rootManifest.EnsureInitializing();
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(journalPath)}.{Guid.NewGuid():N}.tmp");
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

                HostManagerDurableRootTransition transition;
                try
                {
                    transition = rootManifest.BeginCanonicalTransition(
                        workspace.Image.AsSpan(0, checked((int)written)));
                    await commitHook.BeforeCanonicalImageCommitAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    throw new NativeTransactionJournalCommitException(
                        NativeTransactionJournalCommitOutcome.NotCommitted,
                        exception);
                }

                try
                {
                    await committer.CommitAsync(
                        temporaryPath,
                        journalPath,
                        workspace.Image.AsMemory(0, checked((int)written)),
                        cancellationToken);
                }
                catch (NativeTransactionJournalCommitException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new NativeTransactionJournalCommitException(
                        NativeTransactionJournalCommitOutcome.CommitAmbiguous,
                        exception);
                }

                try
                {
                    rootManifest.FinalizeCanonicalTransition(transition);
                }
                catch (Exception exception)
                {
                    throw new NativeTransactionJournalCommitException(
                        NativeTransactionJournalCommitOutcome.CommitAmbiguous,
                        exception);
                }
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
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

internal sealed class NativeTransactionJournalPathLease(FileStream stream) : IDisposable
{
    private FileStream? stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public void Dispose()
        => Interlocked.Exchange(ref stream, null)?.Dispose();
}

internal sealed class NativeTransactionJournalOwnerLeaseException(
    string leasePath,
    Exception innerException)
    : IOException(
        $"The transaction journal owner lease is already held or unavailable: {leasePath}",
        innerException);

internal sealed record NativeTransactionJournalOpenResult(
    NativeTransactionJournalStatus Status,
    NativeTransactionJournalSession? Session);

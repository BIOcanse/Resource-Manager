using System.Buffers;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum NativeTransactionJournalCommitOutcome
{
    NotCommitted = 1,
    CommitAmbiguous = 2
}

internal sealed class NativeTransactionJournalCommitException(
    NativeTransactionJournalCommitOutcome outcome,
    Exception innerException)
    : IOException(
        outcome == NativeTransactionJournalCommitOutcome.CommitAmbiguous
            ? "The transaction journal canonical image may have been committed, but verification failed."
            : "The transaction journal canonical image was not committed.",
        innerException)
{
    internal NativeTransactionJournalCommitOutcome Outcome { get; } = outcome;

    internal static bool IsCommitAmbiguous(Exception exception)
        => Classify(exception).Ambiguous;

    internal static bool IsDefinitelyNotCommitted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var classification = Classify(exception);
        return classification.NotCommitted && !classification.Ambiguous;
    }

    private static (bool NotCommitted, bool Ambiguous) Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var pending = new Stack<Exception>();
        var notCommitted = false;
        var ambiguous = false;
        pending.Push(exception);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (current is NativeTransactionJournalCommitException commitException)
            {
                if (commitException.Outcome ==
                    NativeTransactionJournalCommitOutcome.CommitAmbiguous)
                {
                    ambiguous = true;
                }
                else if (commitException.Outcome ==
                    NativeTransactionJournalCommitOutcome.NotCommitted)
                {
                    notCommitted = true;
                }
            }
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
        return (notCommitted, ambiguous);
    }
}

internal interface INativeTransactionJournalFileCommitter
{
    ValueTask CommitAsync(
        string temporaryPath,
        string journalPath,
        ReadOnlyMemory<byte> expectedImage,
        CancellationToken cancellationToken);
}

internal interface INativeTransactionJournalCommitHook
{
    ValueTask BeforeCanonicalImageCommitAsync(CancellationToken cancellationToken);
}

internal sealed class NativeTransactionJournalCommitHook : INativeTransactionJournalCommitHook
{
    public static NativeTransactionJournalCommitHook None { get; } = new();

    private NativeTransactionJournalCommitHook()
    {
    }

    public ValueTask BeforeCanonicalImageCommitAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class WindowsNativeTransactionJournalFileCommitter
    : INativeTransactionJournalFileCommitter
{
    private const int IoBufferSize = 64 * 1024;

    public static WindowsNativeTransactionJournalFileCommitter Instance { get; } = new();

    private WindowsNativeTransactionJournalFileCommitter()
    {
    }

    public async ValueTask CommitAsync(
        string temporaryPath,
        string journalPath,
        ReadOnlyMemory<byte> expectedImage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, journalPath);
        }
        catch (Exception exception)
        {
            throw new NativeTransactionJournalCommitException(
                NativeTransactionJournalCommitOutcome.NotCommitted,
                exception);
        }

        try
        {
            await VerifyCommittedImageAsync(journalPath, expectedImage, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new NativeTransactionJournalCommitException(
                NativeTransactionJournalCommitOutcome.CommitAmbiguous,
                exception);
        }
    }

    private static async Task VerifyCommittedImageAsync(
        string journalPath,
        ReadOnlyMemory<byte> expectedImage,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            journalPath,
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
                "The committed transaction journal length does not match the native image.");
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
                    !buffer.AsSpan(0, read).SequenceEqual(expectedImage.Span.Slice(offset, read)))
                {
                    throw new InvalidDataException(
                        "The committed transaction journal differs from the native canonical image.");
                }

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

}

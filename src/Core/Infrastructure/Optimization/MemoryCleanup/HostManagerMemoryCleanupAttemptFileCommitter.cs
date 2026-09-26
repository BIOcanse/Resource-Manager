using System.Buffers;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerMemoryCleanupAttemptCommitOutcome : byte
{
    NotCommitted = 1,
    CommitAmbiguous = 2
}

internal sealed class HostManagerMemoryCleanupAttemptCommitException(
    HostManagerMemoryCleanupAttemptCommitOutcome outcome,
    Exception innerException)
    : IOException(
        outcome == HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous
            ? "The memory-cleanup attempt journal may have been committed, but verification failed."
            : "The memory-cleanup attempt journal was not committed.",
        innerException)
{
    internal HostManagerMemoryCleanupAttemptCommitOutcome Outcome { get; } = outcome;
}

internal interface IHostManagerMemoryCleanupAttemptFileCommitter
{
    void Commit(
        string temporaryPath,
        string canonicalPath,
        ReadOnlySpan<byte> expectedImage);
}

internal interface IHostManagerMemoryCleanupLegacyFileRetirer
{
    WindowsNativeFileDeleteResult Retire(string path);
}

internal sealed class WindowsHostManagerMemoryCleanupLegacyFileRetirer
    : IHostManagerMemoryCleanupLegacyFileRetirer
{
    internal static WindowsHostManagerMemoryCleanupLegacyFileRetirer Instance { get; } = new();

    private WindowsHostManagerMemoryCleanupLegacyFileRetirer()
    {
    }

    public WindowsNativeFileDeleteResult Retire(string path)
        => WindowsNativeAtomicFileCommitter.DeleteExact(path);
}

internal sealed class WindowsHostManagerMemoryCleanupAttemptFileCommitter
    : IHostManagerMemoryCleanupAttemptFileCommitter
{
    private const int IoBufferSize = 64 * 1024;

    internal static WindowsHostManagerMemoryCleanupAttemptFileCommitter Instance { get; } = new();

    private WindowsHostManagerMemoryCleanupAttemptFileCommitter()
    {
    }

    public void Commit(
        string temporaryPath,
        string canonicalPath,
        ReadOnlySpan<byte> expectedImage)
    {
        try
        {
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, canonicalPath);
        }
        catch (Exception exception)
        {
            throw new HostManagerMemoryCleanupAttemptCommitException(
                HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                exception);
        }

        try
        {
            VerifyCommittedImage(canonicalPath, expectedImage);
        }
        catch (Exception exception)
        {
            throw new HostManagerMemoryCleanupAttemptCommitException(
                HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                exception);
        }
    }

    private static void VerifyCommittedImage(
        string canonicalPath,
        ReadOnlySpan<byte> expectedImage)
    {
        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.SequentialScan);
        if (stream.Length != expectedImage.Length)
        {
            throw new InvalidDataException(
                "The committed memory-cleanup attempt journal has an unexpected length.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            var offset = 0;
            while (offset < expectedImage.Length)
            {
                var length = Math.Min(buffer.Length, expectedImage.Length - offset);
                var read = stream.Read(buffer, 0, length);
                if (read == 0
                    || !buffer.AsSpan(0, read).SequenceEqual(
                        expectedImage.Slice(offset, read)))
                {
                    throw new InvalidDataException(
                        "The committed memory-cleanup attempt journal differs from its expected image.");
                }

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

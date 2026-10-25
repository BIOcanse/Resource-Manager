using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class WindowsNativeAtomicFileCommitter
{
    // Readers share write and delete, including the committer's still-open handle after publication.
    internal const FileShare CommittedReadShare = FileShare.ReadWrite | FileShare.Delete;
    internal delegate int ReadOpenCall(string nativePath, FileOptions options, out SafeFileHandle file);

    /// <summary>
    /// Opens one whole committed version, sharing read, write and delete. Only the native open's
    /// STATUS_OBJECT_NAME_NOT_FOUND is retried (three calls, 10 ms apart) for the observed POSIX
    /// rename lookup window. Every retry and recovery/exhaustion is traced. A genuinely missing
    /// file still fails; reading never restarts after acquiring the handle.
    /// </summary>
    public static FileStream OpenCommittedRead(string path, FileOptions options = FileOptions.None)
        => OpenCommittedRead(path, options, OpenReadNative);

    internal static FileStream OpenCommittedRead(string path, FileOptions options, ReadOpenCall open)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Committed reads require Windows.");
        const FileOptions validOptions = FileOptions.Asynchronous | FileOptions.DeleteOnClose |
            FileOptions.Encrypted | FileOptions.RandomAccess | FileOptions.SequentialScan | FileOptions.WriteThrough;
        if ((options & ~validOptions) != 0) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var nativePath = ToNtPath(ToExtendedLengthPath(fullPath));
        for (var attempt = 1; ; attempt++)
        {
            var status = open(nativePath, options, out var file);
            if (status >= 0)
            {
                try
                {
                    if (attempt > 1) LogTransient("open", status, path, attempt, "recovered");
                    return new CommittedReadStream(file, fullPath, (options & FileOptions.Asynchronous) != 0);
                }
                catch { file.Dispose(); throw; }
            }
            file.Dispose();
            var retry = status == StatusObjectNameNotFound && attempt < TransientAttemptLimit;
            LogTransient("open", status, path, attempt,
                retry ? "retry" : status == StatusObjectNameNotFound ? "exhausted" : "failed");
            if (!retry) throw ReadOpenError(path, status);
            Thread.Sleep(TransientRetryDelayMilliseconds);
        }
    }

    public static byte[] ReadCommittedBytes(string path)
    {
        using var stream = OpenCommittedRead(path);
        var content = new byte[CommittedLength(stream)];
        stream.ReadExactly(content);
        return content;
    }

    public static async Task<byte[]> ReadCommittedBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenCommittedRead(path, FileOptions.Asynchronous);
        var content = new byte[CommittedLength(stream)];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    private static int CommittedLength(FileStream stream)
        => stream.Length <= Array.MaxLength
            ? (int)stream.Length
            : throw new IOException("The committed file is too large to read into one buffer.");

    // A handle-backed FileStream otherwise loses the pathname exposed by its Name property.
    private sealed class CommittedReadStream(SafeFileHandle handle, string path, bool isAsync)
        : FileStream(handle, FileAccess.Read, 4096, isAsync)
    {
        public override string Name => path;
    }

    private static Exception ReadOpenError(string path, int status)
    {
        var error = unchecked((int)RtlNtStatusToDosError(status));
        var message = $"Cannot open committed file '{path}' (NTSTATUS=0x{status:X8}): {new Win32Exception(error).Message}";
        // FileStream reports opening a directory for file data as access denied.
        if (status == unchecked((int)0xC00000BA)) return new UnauthorizedAccessException(message);
        return error switch
        {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message),
            206 => new PathTooLongException(message),
            _ => new IOException(message, unchecked((int)0x80070000) | error)
        };
    }
}

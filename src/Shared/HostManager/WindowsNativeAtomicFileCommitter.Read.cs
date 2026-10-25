namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class WindowsNativeAtomicFileCommitter
{
    // CommitReplace swaps a complete file in by POSIX rename, so a reader always sees one whole committed
    // version. Readers must share write and delete: a handle that shares only read makes the next replace
    // fail, and right after the rename the committer's own handle still holds write access to the new file.
    internal const FileShare CommittedReadShare = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream OpenCommittedRead(string path, FileOptions options = FileOptions.None)
        => new(path, FileMode.Open, FileAccess.Read, CommittedReadShare, 4096, options);

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
}

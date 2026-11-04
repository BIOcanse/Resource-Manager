using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

// Shared by the two one-time optimization migrations; final backup names are immutable.
internal static class OptimizationDisplayTextBackup
{
    internal static string Preserve(string sourcePath, byte[] image,
        Action<Stream, byte[]>? writeTemporary = null)
    {
        var stem = sourcePath + ".display-text.byte-exact." + Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        for (var index = 0; index < 32; index++)
        {
            var backup = stem + (index == 0 ? string.Empty : "." + index) + ".backup";
            if (File.Exists(backup))
            {
                if (image.AsSpan().SequenceEqual(WindowsNativeAtomicFileCommitter.ReadCommittedBytes(backup)))
                    return backup;
                continue; // Preserve different earlier bytes, including interrupted pre-fix backups.
            }
            var temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (writeTemporary is null) output.Write(image);
                    else writeTemporary(output, image);
                    output.Flush(flushToDisk: true);
                }
                // Never publish a partial write, including one that returned without throwing.
                if (!image.AsSpan().SequenceEqual(WindowsNativeAtomicFileCommitter.ReadCommittedBytes(temporary)))
                    throw new IOException("Optimization migration temporary backup differs from its source.");
                try { WindowsNativeAtomicFileCommitter.CommitNew(temporary, backup); }
                catch (IOException) when (File.Exists(backup))
                {
                    if (image.AsSpan().SequenceEqual(WindowsNativeAtomicFileCommitter.ReadCommittedBytes(backup)))
                        return backup;
                    continue;
                }
                if (!image.AsSpan().SequenceEqual(WindowsNativeAtomicFileCommitter.ReadCommittedBytes(backup)))
                    throw new IOException("Optimization migration backup differs from its source.");
                return backup;
            }
            finally { File.Delete(temporary); }
        }
        throw new IOException("No unused optimization migration backup name remains; earlier backups were preserved.");
    }
}

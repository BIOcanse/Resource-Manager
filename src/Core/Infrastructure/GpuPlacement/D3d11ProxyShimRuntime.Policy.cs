using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class D3d11ProxyShimRuntime
{
    private readonly object policySync = new();

    internal string GetPolicyPath(string targetId)
        => Path.Combine(policyRoot, CreateStableDirectoryName(targetId), "gpu-policy.txt");

    // Null means no file; an empty array means an existing zero-byte file.
    internal byte[]? ReadPolicy(string targetId)
    {
        lock (policySync)
        {
            return ReadPolicyCore(GetPolicyPath(targetId));
        }
    }

    // The placement owner saves expected/current values in its ledger before writing.
    // The same operation restores previous bytes by comparing against applied bytes.
    internal bool TryWritePolicy(string targetId, byte[]? expectedValue, byte[]? value)
    {
        lock (policySync)
        {
            var path = GetPolicyPath(targetId);
            var current = ReadPolicyCore(path);
            if (!EqualPolicyBytes(current, expectedValue)) return false;
            if (EqualPolicyBytes(current, value)) return true;

            if (value is null)
            {
                WindowsNativeAtomicFileCommitter.DeleteExact(path);
            }
            else
            {
                WritePolicyCore(path, value);
            }
            return true;
        }
    }

    private static bool EqualPolicyBytes(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static byte[]? ReadPolicyCore(string path)
    {
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var content = new MemoryStream();
            reader.CopyTo(content);
            return content.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void WritePolicyCore(string path, byte[] value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var writer = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writer.Write(value);
            writer.Flush(flushToDisk: true);
        }
        // A failed commit leaves the old file and the prepared file intact.
        WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
    }
}

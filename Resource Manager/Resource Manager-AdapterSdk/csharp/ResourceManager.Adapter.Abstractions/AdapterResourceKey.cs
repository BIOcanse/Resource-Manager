using System.Text;

namespace ResourceManager.Adapter;

public static class AdapterResourceKey
{
    public const ulong OffsetBasis = 14695981039346656037UL;
    public const ulong Prime = 1099511628211UL;

    public static ulong FromString(string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
        {
            throw new ArgumentException("Stable id is required.", nameof(stableId));
        }

        return FromUtf8(Encoding.UTF8.GetBytes(stableId.Trim()));
    }

    public static ulong FromUtf8(ReadOnlySpan<byte> bytes)
    {
        var hash = OffsetBasis;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash == 0 ? OffsetBasis : hash;
    }
}

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeStableIdentity
{
    public static ulong CreateCaseInsensitiveKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in value)
        {
            var normalized = char.ToUpperInvariant(character);
            hash ^= (byte)normalized;
            hash *= prime;
            hash ^= (byte)(normalized >> 8);
            hash *= prime;
        }

        return hash == 0 ? 1 : hash;
    }
}

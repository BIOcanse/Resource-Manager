using System.Buffers.Binary;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

internal abstract record OpenGlCallbackPreparationReply
{
    internal sealed record Success(PreparedOpenGlCallbacks Source) : OpenGlCallbackPreparationReply;
    internal sealed record Failure(uint Error, uint CleanupError) : OpenGlCallbackPreparationReply;
}

internal static class OpenGlCallbackPreparationProtocol
{
    internal const int MaximumMessageBytes = 16 + 4 + 9 * 8 + 9 * (46 + 32767 * 2);

    internal static byte[] Request(ulong sourceAdapterLuid)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), sourceAdapterLuid);
        return bytes;
    }

    internal static OpenGlCallbackPreparationReply ReadReply(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumMessageBytes) throw new InvalidDataException("OpenGL preparation reply exceeds its type bound.");
        var reader = new Reader(data);
        if (reader.UInt32() != 1 || reader.UInt32() != 2) throw new InvalidDataException("Invalid OpenGL preparation reply header.");
        var error = reader.UInt32();
        var cleanupError = reader.UInt32();
        if (error != 0 || cleanupError != 0)
        {
            reader.RequireEnd();
            return new OpenGlCallbackPreparationReply.Failure(error, cleanupError);
        }
        var source = data[16..];
        var count = reader.UInt32();
        if (count is 0 or > 9) throw new InvalidDataException("Invalid callback module count.");
        Span<uint> modules = stackalloc uint[9], offsets = stackalloc uint[9], imageBytes = stackalloc uint[9];
        Span<bool> used = stackalloc bool[9];
        used.Clear();
        for (var slot = 0; slot < 9; slot++) { modules[slot] = reader.UInt32(); offsets[slot] = reader.UInt32(); }
        for (var module = 0; module < count; module++)
        {
            if (reader.UInt16() != 0x8664) throw new InvalidDataException("Callback source must match the x64 provider.");
            imageBytes[module] = reader.UInt32();
            if (imageBytes[module] == 0) throw new InvalidDataException("Empty callback module image.");
            _ = reader.UInt32(); // Native PE timestamp is a value, including zero.
            var characters = reader.UInt32();
            if (characters is 0 or > 32767) throw new InvalidDataException("Invalid callback module path length.");
            _ = reader.Take(32);
            var path = reader.Take(checked((int)characters * 2));
            for (var i = 0; i < path.Length; i += 2)
                if (BinaryPrimitives.ReadUInt16LittleEndian(path[i..]) == 0) throw new InvalidDataException("Callback module path contains NUL.");
        }
        for (var slot = 0; slot < 9; slot++)
        {
            var module = modules[slot];
            if (module == 0)
            {
                if (offsets[slot] != 0 || slot is 0 or 1 or 2 or 5) throw new InvalidDataException("Invalid absent callback entry.");
            }
            else
            {
                if (module > count || offsets[slot] >= imageBytes[(int)module - 1]) throw new InvalidDataException("Callback module offset is out of range.");
                used[(int)module - 1] = true;
            }
        }
        for (var i = 0; i < count; i++) if (!used[i]) throw new InvalidDataException("Unused callback module.");
        reader.RequireEnd();
        return new OpenGlCallbackPreparationReply.Success(new(source));
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> remaining = data;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count > remaining.Length) throw new InvalidDataException("Truncated OpenGL preparation reply.");
            var value = remaining[..count]; remaining = remaining[count..]; return value;
        }
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        internal uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal void RequireEnd() { if (!remaining.IsEmpty) throw new InvalidDataException("Trailing OpenGL preparation data."); }
    }
}

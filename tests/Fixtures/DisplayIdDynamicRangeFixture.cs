namespace ResourceManager.TestFixtures;

internal static class DisplayIdDynamicRangeFixture
{
    // LE24 codes59999/599999:60MHz..600MHz;48..360Hz;seamless true.
    internal static byte[] Payload() => [0x5F, 0xEA, 0, 0xBF, 0x27, 9, 48, 104, 0x81];

    internal static byte[] Bytes(byte[]? payload = null, byte flags = 1, byte version = 0x20,
        bool continuation = false, bool duplicate = false)
    {
        payload ??= Payload();
        if (payload.Length > 40) throw new ArgumentOutOfRangeException(nameof(payload));
        var bytes = new byte[continuation ? 384 : 256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[126] = continuation ? (byte)2 : (byte)1;
        for (var n = 38; n < 54; n += 2) { bytes[n] = 1; bytes[n + 1] = 1; }
        for (var n = 128; n < bytes.Length; n += 128) {
            bytes[n] = 0x70; bytes[n + 1] = version; bytes[n + 3] = n == 128 ? (byte)2 : (byte)0;
        }
        // Structure2.0 EDID sections all carry Extension Count0 (not a native chain).
        bytes[132] = 0;
        var section = continuation ? 256 : 128;
        var extent = checked(payload.Length + 3);
        bytes[section + 2] = checked((byte)(extent * (duplicate ? 2 : 1)));
        for (var n = 0; n < (duplicate ? 2 : 1); n++) {
            var start = section + 5 + n * extent;
            bytes[start] = 0x25; bytes[start + 1] = flags; bytes[start + 2] = checked((byte)payload.Length);
            payload.CopyTo(bytes, start + 3);
        }
        Checksums(bytes);
        return bytes;
    }

    internal static void Checksums(byte[] bytes)
    {
        for (var block = 128; block < bytes.Length; block += 128) {
            var end = block + 5 + bytes[block + 2]; bytes[end] = 0;
            var sum = 0; for (var n = block + 1; n < end; n++) sum += bytes[n];
            bytes[end] = unchecked((byte)-sum);
        }
        for (var block = 0; block < bytes.Length; block += 128) {
            bytes[block + 127] = 0; var sum = 0;
            for (var n = block; n < block + 127; n++) sum += bytes[n];
            bytes[block + 127] = unchecked((byte)-sum);
        }
    }
}

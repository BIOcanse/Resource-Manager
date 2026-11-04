namespace ResourceManager.ReadWrite.Vendors.Intel;

public static class IntelRaplEncoding
{
    public const uint UnitsRegister = 0x606;
    public const uint LimitRegister = 0x610;
    public static double PowerUnit(ulong units) => Math.ScaleB(1d, -(int)(units & 15));
    public static double TimeUnit(ulong units) => Math.ScaleB(1d, -(int)((units >> 16) & 15));
    public static double ReadPower(ulong limits, ulong units, bool shortTerm)
        => ((limits >> (shortTerm ? 32 : 0)) & 0x7fff) * PowerUnit(units);
    public static double DecodeWindow(int encoded, ulong units)
        => Math.ScaleB(1d, encoded & 31) * (1 + ((encoded >> 5) & 3) / 4d) * TimeUnit(units);
    public static double ReadWindow(ulong limits, ulong units, bool shortTerm)
        => DecodeWindow((int)((limits >> (shortTerm ? 49 : 17)) & 127), units);
    public static ulong FieldMask(bool window, bool shortTerm)
        => window ? 0x7fUL << (shortTerm ? 49 : 17) : 0xffffUL << (shortTerm ? 32 : 0);

    public static ulong SetPower(ulong original, ulong units, bool shortTerm, double watts)
    {
        if (!double.IsFinite(watts) || watts <= 0) throw new ArgumentOutOfRangeException(nameof(watts));
        var encoded = Math.Round(watts / PowerUnit(units), MidpointRounding.AwayFromZero);
        if (encoded is < 1 or > 0x7fff) throw new ArgumentOutOfRangeException(nameof(watts));
        var shift = shortTerm ? 32 : 0;
        // Preserve clamp, time window, the other limit and every reserved bit.
        return (original & ~FieldMask(false, shortTerm)) | (((ulong)encoded | 0x8000UL) << shift);
    }

    public static ulong SetWindow(ulong original, ulong units, bool shortTerm, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var best = 0;
        var distance = double.PositiveInfinity;
        for (var candidate = 0; candidate < 128; candidate++)
        {
            var delta = Math.Abs(DecodeWindow(candidate, units) - seconds);
            if (delta < distance) { distance = delta; best = candidate; }
        }
        return (original & ~FieldMask(true, shortTerm)) | ((ulong)best << (shortTerm ? 49 : 17));
    }
}

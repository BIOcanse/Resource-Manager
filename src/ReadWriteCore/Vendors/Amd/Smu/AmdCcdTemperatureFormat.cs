using System;

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

/// <summary>Known SMN CCD sensor register layout, independent of PM table versions.
/// Offsets are compatibility data from k10temp/LHM, not an AMD datasheet guarantee.</summary>
public sealed class AmdCcdTemperatureFormat
{
    private AmdCcdTemperatureFormat(uint offset, int maximumSensorCount)
    {
        RegisterBase = 0x00059800 + offset;
        MaximumSensorCount = maximumSensorCount;
    }

    public uint RegisterBase { get; }
    public int MaximumSensorCount { get; }

    /// <summary>The caller must already have identified an AMD processor.
    /// Unknown families/models stay unavailable; Hygon's separate vendor is not inferred.</summary>
    public static AmdCcdTemperatureFormat? Resolve(uint family, uint model)
        => (family, model) switch
        {
            (0x17, 0x01 or 0x08 or 0x11 or 0x18) => new(0x154, 4),
            (0x17, 0x31 or 0x47 or 0x60 or 0x68 or 0x71) => new(0x154, 8),
            (0x17, >= 0xA0 and <= 0xAF) => new(0x300, 8),
            (0x19, 0x00 or 0x01 or 0x08 or 0x21) => new(0x154, 8),
            (0x19, >= 0x50 and <= 0x5F) => new(0x154, 8),
            (0x19, >= 0x40 and <= 0x4F) => new(0x300, 8),
            (0x19, >= 0x60 and <= 0x7F) => new(0x308, 8),
            (0x19, >= 0x10 and <= 0x1F) => new(0x300, 12),
            (0x19, >= 0xA0 and <= 0xAF) => new(0x300, 12),
            (0x1A, >= 0x00 and <= 0x1F) => new(0x1F0, 16),
            (0x1A, >= 0x40 and <= 0x4F) => new(0x308, 8),
            _ => null
        };

    public uint RegisterAddress(int sensorIndex)
    {
        if ((uint)sensorIndex >= (uint)MaximumSensorCount)
            throw new ArgumentOutOfRangeException(nameof(sensorIndex));
        return RegisterBase + (uint)sensorIndex * sizeof(uint);
    }

    public static double? Decode(uint register)
        => register == uint.MaxValue || (register & 0x800) == 0
            ? null
            : (register & 0x7FF) * 0.125 - 49;
}

namespace ResourceManager.App.Infrastructure.Control.Writers.Intel;

/// <summary>OC mailbox voltage offsets, in signed 1/1024 V units. Queries never change an offset.</summary>
internal static class IntelOcMailbox
{
    internal const uint Register = 0x150;
    private const ulong Busy = 1UL << 63;
    internal static int? Plane(string id) => id switch
    {
        "cpu.core-voltage-offset" => 0,
        "cpu.igpu-voltage-offset" => 1,
        "cpu.cache-voltage-offset" => 2,
        "cpu.system-agent-voltage-offset" => 3,
        _ => null
    };
    internal static uint Encode(double millivolts)
    {
        if (!double.IsFinite(millivolts)) throw new ArgumentOutOfRangeException(nameof(millivolts));
        var value = (int)Math.Round(millivolts * 1.024);
        if (value is < -1024 or > 1023) throw new ArgumentOutOfRangeException(nameof(millivolts));
        return ((uint)value & 0x7ff) << 21;
    }
    internal static double Decode(uint data) => ((int)data >> 21) / 1.024;
    internal static uint Read(IIntelCpuHardware io, int plane) => Execute(io, plane, false, 0);
    internal static void Write(IIntelCpuHardware io, int plane, uint data) => Execute(io, plane, true, data);
    private static uint Execute(IIntelCpuHardware io, int plane, bool write, uint data)
    {
        if ((io.ReadMsr(Register) & Busy) != 0) throw new IOException("Intel OC 邮箱正忙，请稍后重试。");
        io.WriteMsr(Register, Busy | ((ulong)plane << 40) | ((write ? 0x11UL : 0x10UL) << 32) | data);
        // Bounded polling; no repeat submission of a hardware write.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = io.ReadMsr(Register);
            if ((result & Busy) != 0) { Thread.SpinWait(100); continue; }
            if (((result >> 32) & 0xff) != 0) throw new IOException($"Intel 固件拒绝 OC 邮箱请求：0x{(result >> 32) & 0xff:X2}。");
            return (uint)result;
        }
        throw new IOException("Intel OC 邮箱未在限定轮询次数内完成请求。");
    }
}

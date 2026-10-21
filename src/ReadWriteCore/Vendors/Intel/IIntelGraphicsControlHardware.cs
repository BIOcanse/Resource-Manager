using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
namespace ResourceManager.ReadWrite.Vendors.Intel;

public interface IIntelGraphicsControlHardware
{
    bool IsAvailable { get; }
    IntPtr? FindIntegratedDevice(ulong? adapterKey);
    bool TryAcceptOverclockWaiver(IntPtr device);
    double? ReadFrequencyOffsetMhz(IntPtr device);
    int WriteFrequencyOffsetMhz(IntPtr device, double offsetMhz);
}

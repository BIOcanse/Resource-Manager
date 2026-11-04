namespace ResourceManager.ReadWrite.Vendors.Intel;

/// <summary>Module operations only. The writer owns ranges, verification and restoration.</summary>
public interface IIntelCpuHardware : IDisposable
{
    ulong ReadMsr(uint register);
    void WriteMsr(uint register, ulong value);
    bool CanWriteMsr(uint register);
}

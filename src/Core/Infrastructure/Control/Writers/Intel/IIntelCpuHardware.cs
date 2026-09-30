namespace ResourceManager.App.Infrastructure.Control.Writers.Intel;

/// <summary>Module operations only. The writer owns ranges, verification and restoration.</summary>
internal interface IIntelCpuHardware : IDisposable
{
    ulong ReadMsr(uint register);
    void WriteMsr(uint register, ulong value);
    bool CanWriteMsr(uint register);
}

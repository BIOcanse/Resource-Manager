namespace ResourceManager.App.Infrastructure.Control.Writers;

internal interface IUniwillGpuPowerHardware
{
    bool IsAvailable { get; }
    byte? Read(ushort address);
    byte? WriteReadingBack(ushort address, byte value);
    NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex);
}

internal sealed class UniwillGpuPowerHardware : IUniwillGpuPowerHardware
{
    private readonly UniwillEcBridge ec = new();
    private readonly NvidiaNvmlControlBridge nvml = new();

    public bool IsAvailable => ec.IsAvailable;
    public byte? Read(ushort address) => ec.Read(address);
    public byte? WriteReadingBack(ushort address, byte value) => ec.WriteReadingBack(address, value);
    public NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex)
        => nvml.FindHandleByAdapterIndex(adapterIndex) is { } device ? nvml.ReadPowerLimit(device) : null;
}

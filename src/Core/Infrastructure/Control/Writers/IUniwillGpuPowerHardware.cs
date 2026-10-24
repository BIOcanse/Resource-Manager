using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Control.Writers;

internal interface IUniwillGpuPowerHardware : IDisposable
{
    bool IsAvailable { get; }
    bool IsBudgetFirmwareConfirmed { get; }
    bool IsBudgetTarget(int adapterIndex, ulong adapterKey);
    byte? Read(ushort address);
    byte? WriteReadingBack(ushort address, byte value);
    NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex);
}

internal sealed class UniwillGpuPowerHardware : IUniwillGpuPowerHardware
{
    private readonly UniwillEcBridge ec = new();
    private readonly NvidiaNvmlControlBridge nvml = new();
    private readonly WindowsGpuAdapterOrderReader adapters = new();
    private readonly object firmwareGate = new();
    private readonly Lazy<ResourceManager.App.Domain.RuntimeSpecialization.FirmwareIdentitySnapshot> firmware
        = new(() => new FirmwareIdentityReader().Read());
    private bool? acpiDevicePresent;
    private bool? budgetFirmwareConfirmed;

    public bool IsAvailable => ec.IsAvailable;
    public bool IsBudgetFirmwareConfirmed
    {
        get
        {
            lock (firmwareGate)
            {
                if (budgetFirmwareConfirmed is { } cached) return cached;
                var confirmed = UniwillGpuPowerFirmwareProfile.Detect(
                    firmware.Value,
                    HasAcpiDevice,
                    ec.Read);
                if (confirmed is { } result) budgetFirmwareConfirmed = result;
                return confirmed is true;
            }
        }
    }

    private bool? HasAcpiDevice()
    {
        if (acpiDevicePresent is { } cached) return cached;
        var devices = PlatformSensorWmiUtilities.QueryObjects(@"root\CIMV2",
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 0");
        if (devices.Error is not null) return null;
        acpiDevicePresent = devices.Rows.Any(static row => UniwillGpuPowerFirmwareProfile.IsAcpiDeviceId(
            PlatformSensorWmiUtilities.ReadString(row, "PNPDeviceID")));
        return acpiDevicePresent;
    }
    public bool IsBudgetTarget(int adapterIndex, ulong adapterKey)
    {
        if (adapterIndex < 0 || adapterKey == 0) return false;
        var inventory = adapters.ReadInventory();
        if (inventory.Status != SamplingObservationStatus.Current || inventory.SkippedCount != 0
            || inventory.OverflowCount != 0 || inventory.ObservedCount != inventory.Adapters.Count) return false;
        var candidates = inventory.Adapters.Where(static adapter => adapter.VendorId == 0x10DE && !adapter.IsSoftware)
            .Select(adapter => new BudgetAdapter(adapter.Index, WindowsGpuAdapterIdentity.Pack(adapter.Luid),
                WindowsGpuIdentityBridge.ReadAdapterType(adapter.Luid))).ToArray();
        return IsUniqueInternalTarget(adapterIndex, adapterKey, candidates);
    }

    internal static bool IsUniqueInternalTarget(int index, ulong key, IReadOnlyList<BudgetAdapter> adapters)
    {
        if (index < 0 || key == 0 || adapters.Count > 128 || adapters.Any(static adapter => adapter.Type is null)) return false;
        BudgetAdapter? selected = null;
        foreach (var adapter in adapters)
        {
            // D3DKMT_ADAPTERTYPE: RenderSupported, HybridDiscrete; exclude SoftwareDevice and Detachable.
            if (adapter.Type is not { } type || (type & 0x11) != 0x11 || (type & 0x404) != 0) continue;
            if (adapter.Key == 0 || selected is not null) return false;
            selected = adapter;
        }
        return selected is { } target && target.Index == index && target.Key == key;
    }

    internal readonly record struct BudgetAdapter(int Index, ulong Key, uint? Type);
    public byte? Read(ushort address) => ec.Read(address);
    public byte? WriteReadingBack(ushort address, byte value) => ec.WriteReadingBack(address, value);
    public NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex)
        => nvml.FindHandleByAdapterIndex(adapterIndex) is { } device ? nvml.ReadPowerLimit(device) : null;

    public void Dispose() => ec.Dispose();
}

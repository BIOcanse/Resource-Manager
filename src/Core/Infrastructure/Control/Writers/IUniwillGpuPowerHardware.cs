using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Control.Writers;

internal interface IUniwillGpuPowerHardware : IDisposable
{
    bool IsAvailable { get; }
    bool IsBudgetFirmwareConfirmed { get; }
    bool IsBudgetTarget(int adapterIndex, ulong adapterKey);
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    byte? Read(ushort address);
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    byte? WriteReadingBack(ushort address, byte value);
    NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex);
}

internal sealed class UniwillGpuPowerHardware : IUniwillGpuPowerHardware
{
    private readonly UniwillEcBridge ec = new();
    private readonly NvidiaNvmlControlBridge nvml = new();
    private readonly WindowsGpuAdapterOrderReader adapters = new();
    private readonly object firmwareGate = new();
    private readonly Func<string, string, QueryResult> query;
    private readonly Lazy<ResourceManager.App.Domain.RuntimeSpecialization.FirmwareIdentitySnapshot> firmware;
    private readonly Func<ushort, byte?> readFirmwareByte;
    private bool? acpiDevicePresent;
    private bool acpiDeviceProbed;
    private bool? budgetFirmwareConfirmed;
    private bool budgetFirmwareProbed;

    public UniwillGpuPowerHardware() : this(PlatformSensorWmiUtilities.QueryObjects) { }
    internal UniwillGpuPowerHardware(Func<string, string, QueryResult> query,
        Func<ResourceManager.App.Domain.RuntimeSpecialization.FirmwareIdentitySnapshot>? readFirmware = null,
        Func<ushort, byte?>? readFirmwareByte = null)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
        firmware = new(readFirmware ?? (() => new FirmwareIdentityReader().Read()));
        this.readFirmwareByte = readFirmwareByte ?? ec.Read;
    }

    public bool IsAvailable => ec.IsAvailable;
    public bool IsBudgetFirmwareConfirmed => ResolveBudgetFirmware() is true;

    [ResourceManager.ReadWrite.RuntimeReadCache]
    private bool? ResolveBudgetFirmware()
    {
        lock (firmwareGate)
        {
            if (budgetFirmwareProbed) return budgetFirmwareConfirmed;
            budgetFirmwareProbed = true;
            return budgetFirmwareConfirmed = ReadBudgetFirmware();
        }
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private bool? ReadBudgetFirmware()
        => UniwillGpuPowerFirmwareProfile.Detect(firmware.Value, HasAcpiDevice, readFirmwareByte);

    [ResourceManager.ReadWrite.RuntimeReadCache]
    internal bool? HasAcpiDevice()
    {
        lock (firmwareGate)
        {
            if (acpiDeviceProbed) return acpiDevicePresent;
            acpiDeviceProbed = true;
            acpiDevicePresent = QueryAcpiDevice();
            return acpiDevicePresent;
        }
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private bool? QueryAcpiDevice()
    {
        var devices = query(@"root\CIMV2",
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 0");
        return devices.Error is not null ? null : devices.Rows.Any(static row => UniwillGpuPowerFirmwareProfile.IsAcpiDeviceId(
            PlatformSensorWmiUtilities.ReadString(row, "PNPDeviceID")));
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
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    public byte? Read(ushort address) => ec.Read(address);
    [ResourceManager.ReadWrite.RuntimeReadTransport]
    public byte? WriteReadingBack(ushort address, byte value) => ec.WriteReadingBack(address, value);
    public NvidiaPowerLimitWatts? ReadPowerLimit(int adapterIndex)
        => nvml.FindHandleByAdapterIndex(adapterIndex) is { } device ? nvml.ReadPowerLimit(device) : null;

    public void Dispose() => ec.Dispose();
}

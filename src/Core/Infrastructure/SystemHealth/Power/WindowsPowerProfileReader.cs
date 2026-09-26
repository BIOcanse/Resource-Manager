using System.Runtime.InteropServices;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.SystemHealth;

namespace ResourceManager.App.Infrastructure.SystemHealth.Power;

public sealed class WindowsPowerProfileReader : IPowerProfileReader
{
    private static readonly Guid ProcessorSettingsSubgroup = new("54533251-82BE-4824-96C1-47B60B740D00");
    private static readonly Guid ProcessorMaximumState = new("BC5038F7-23E0-4960-96DA-33ABAF5935EC");
    private static readonly Guid ProcessorEnergyPreference = new("36687F9E-E3A5-4DBF-B1DC-15EB381C6863");
    private static readonly Guid ProcessorBoostMode = new("BE337238-0D82-4146-A960-4F3749D470C7");
    private static readonly Guid DiskSettingsSubgroup = new("0012EE47-9041-4B5D-9B77-535FBA8B1442");
    private static readonly Guid DiskIdleTimeout = new("6738E2C4-E8A5-4A42-B16A-E040E769756E");
    private static readonly IReadOnlyDictionary<Guid, string> KnownSchemeNames = new Dictionary<Guid, string>
    {
        [new Guid("381B4222-F694-41F0-9685-FF5BB260DF2E")] = "平衡",
        [new Guid("8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C")] = "高性能",
        [new Guid("A1841308-3541-4FAB-BC81-F71556F20B4A")] = "节能",
        [new Guid("E9A42B02-D5DF-448D-AA00-03F14749EB61")] = "卓越性能"
    };

    public PowerProfileSnapshot Read()
    {
        if (!GetSystemPowerStatus(out var systemPowerStatus))
        {
            return PowerProfileSnapshot.Unavailable("GetSystemPowerStatus failed.");
        }

        var schemeResult = PowerGetActiveScheme(nint.Zero, out var schemePointer);
        if (schemeResult != 0 || schemePointer == nint.Zero)
        {
            return PowerProfileSnapshot.Unavailable($"PowerGetActiveScheme failed: {schemeResult}.");
        }

        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            return new PowerProfileSnapshot(
                DateTimeOffset.UtcNow,
                true,
                systemPowerStatus.AcLineStatus == 1,
                systemPowerStatus.SystemStatusFlag != 0,
                scheme,
                KnownSchemeNames.GetValueOrDefault(scheme, scheme.ToString("D")),
                ReadAcValue(scheme, ProcessorSettingsSubgroup, ProcessorMaximumState),
                ReadAcValue(scheme, ProcessorSettingsSubgroup, ProcessorEnergyPreference),
                ReadAcValue(scheme, ProcessorSettingsSubgroup, ProcessorBoostMode),
                ReadAcValue(scheme, DiskSettingsSubgroup, DiskIdleTimeout),
                ReadDcValue(scheme, DiskSettingsSubgroup, DiskIdleTimeout),
                null);
        }
        finally
        {
            LocalFree(schemePointer);
        }
    }

    private static uint? ReadAcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        return PowerReadACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0
            ? value
            : null;
    }

    private static uint? ReadDcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        return PowerReadDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0
            ? value
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        nint rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        nint rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

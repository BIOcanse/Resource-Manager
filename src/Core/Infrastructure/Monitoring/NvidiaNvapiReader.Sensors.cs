using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class NvidiaNvapiReader
{
    private const int ClientFanCoolersStatusSize = 1704;
    private const int ClientFanCoolersStatusCountOffset = 4;
    private const int ClientFanCoolersStatusItemsOffset = 40;
    private const int ClientFanCoolersStatusItemSize = 52;
    private const int ClientFanCoolersStatusItemRpmOffset = 4;
    private const int MaxClientFanCoolersStatusItems = 32;
    private const int ClientVoltRailsStatusSize = 0x4C;
    private const int ClientVoltRailsCoreMicrovoltsOffset = 0x28;
    private const int ClientVoltRailsCoreMicrovoltsHighOffset = 0x2C;
    private const int Pstates20V1Size = 7316;
    private const int Pstates20V2Size = 7416;
    private const int Pstates20HeaderSize = 20;
    private const int Pstate20EntrySize = 456;
    private const int BaseVoltageEntrySize = 24;
    private const int Pstate20PstatesOffset = 20;
    private const int Pstate20EntryIdOffset = 0;
    private const int Pstate20BaseVoltagesOffset = 360;
    private const int BaseVoltageDomainOffset = 0;
    private const int BaseVoltageMicrovoltsOffset = 8;
    private const int MaxPstates20Entries = 16;
    private const int MaxPstates20BaseVoltages = 4;

    private NvidiaNvapiGpuSensor? ReadDevice(
        IntPtr gpuHandle,
        NvidiaNvapiReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory)
    {
        var name = ReadNvapiShortString(gpuHandle);
        var pciInfo = ReadPciIdentifiers(gpuHandle);
        var pciLocation = ReadPciLocation(gpuHandle);
        if (pciInfo is null
            || pciLocation is null
            || !WindowsGpuProviderIdentityResolver.TryCreateNvapiPciEvidence(
                pciLocation.Value.BusId,
                pciLocation.Value.BusSlotId,
                pciInfo.Value.DeviceId,
                pciInfo.Value.SubSystemId,
                out var evidence))
        {
            return null;
        }

        var match = WindowsGpuProviderIdentityResolver.ResolvePci(
            windowsInventory,
            evidence);
        if (!match.IsCurrent
            || !request.IncludesDisplayIndex(match.Binding.Adapter.Index))
        {
            return null;
        }

        var sensors = ReadSensors(gpuHandle, request);
        return HasAnySensorValue(sensors)
            ? new NvidiaNvapiGpuSensor(
                match.Binding.Adapter.Index,
                string.IsNullOrWhiteSpace(name)
                    ? match.Binding.Adapter.Name
                    : name,
                sensors)
            : null;
    }

    private GpuSensorMetrics ReadSensors(IntPtr gpuHandle, NvidiaNvapiReadRequest request)
    {
        var fanRpm = request.IncludeFanRpm ? ReadFanRpm(gpuHandle) : null;
        var voltage = request.IncludeVoltage ? ReadCoreVoltageVolts(gpuHandle) : null;
        var hasAnyValue = fanRpm is not null || voltage is not null;
        var state = hasAnyValue ? "Partial" : "Unavailable";
        var message = hasAnyValue
            ? "NVIDIA NVAPI 返回部分补充传感读数；风扇和电流取决于驱动是否暴露可靠字段。"
            : "NVIDIA NVAPI 可加载，但当前驱动/硬件未返回请求的补充传感读数。";

        return new GpuSensorMetrics(
            new HardwareSensorProviderState("NVIDIA NVAPI", state, message),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            fanRpm,
            voltage,
            null);
    }

    private double? ReadFanRpm(IntPtr gpuHandle)
    {
        return ReadClientFanRpm(gpuHandle) ?? ReadTachFanRpm(gpuHandle);
    }

    private double? ReadClientFanRpm(IntPtr gpuHandle)
    {
        if (bindings.ClientFanCoolersGetStatus is null)
        {
            return null;
        }

        var buffer = AllocateZeroedBuffer(ClientFanCoolersStatusSize);
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)MakeNvapiVersion(ClientFanCoolersStatusSize, 1)));

            try
            {
                if (bindings.ClientFanCoolersGetStatus(gpuHandle, buffer) != NvapiOk)
                {
                    return null;
                }
            }
            catch (AccessViolationException)
            {
                return null;
            }

            var count = ClampStructCount(ReadUInt32(buffer, ClientFanCoolersStatusCountOffset), MaxClientFanCoolersStatusItems);
            if (count == 0)
            {
                return null;
            }

            double total = 0;
            for (var index = 0; index < count; index++)
            {
                var itemOffset = ClientFanCoolersStatusItemsOffset + (index * ClientFanCoolersStatusItemSize);
                total += ReadUInt32(buffer, itemOffset + ClientFanCoolersStatusItemRpmOffset);
            }

            return total / count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private double? ReadTachFanRpm(IntPtr gpuHandle)
    {
        try
        {
            return bindings.GetTachReading is not null
                && bindings.GetTachReading(gpuHandle, out var rpm) == NvapiOk
                    ? rpm
                    : null;
        }
        catch (AccessViolationException)
        {
            return null;
        }
    }

    private double? ReadCoreVoltageVolts(IntPtr gpuHandle)
    {
        var clientVoltage = ReadClientCoreVoltageVolts(gpuHandle);
        if (clientVoltage is not null)
        {
            return clientVoltage;
        }

        if (bindings.GetPstates20 is null)
        {
            return null;
        }

        uint? currentPstate = null;
        try
        {
            if (bindings.GetCurrentPstate is not null
                && bindings.GetCurrentPstate(gpuHandle, out var pstate) == NvapiOk
                && pstate < MaxPstates20Entries)
            {
                currentPstate = pstate;
            }
        }
        catch (AccessViolationException)
        {
            currentPstate = null;
        }

        return ReadCoreVoltageVolts(gpuHandle, currentPstate, MakeNvapiVersion(Pstates20V2Size, 3))
            ?? ReadCoreVoltageVolts(gpuHandle, currentPstate, MakeNvapiVersion(Pstates20V2Size, 2))
            ?? ReadCoreVoltageVolts(gpuHandle, currentPstate, MakeNvapiVersion(Pstates20V1Size, 1));
    }

    private double? ReadClientCoreVoltageVolts(IntPtr gpuHandle)
    {
        if (bindings.ClientVoltRailsGetStatus is null)
        {
            return null;
        }

        var buffer = AllocateZeroedBuffer(ClientVoltRailsStatusSize);
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)MakeNvapiVersion(ClientVoltRailsStatusSize, 1)));

            try
            {
                if (bindings.ClientVoltRailsGetStatus(gpuHandle, buffer) != NvapiOk)
                {
                    return null;
                }
            }
            catch (AccessViolationException)
            {
                return null;
            }

            var low = ReadUInt32(buffer, ClientVoltRailsCoreMicrovoltsOffset);
            var high = ReadUInt32(buffer, ClientVoltRailsCoreMicrovoltsHighOffset);
            var rawMicrovolts = ((ulong)high << 32) | low;
            return rawMicrovolts == 0 ? 0 : NormalizeVoltageVolts(rawMicrovolts);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private double? ReadCoreVoltageVolts(IntPtr gpuHandle, uint? currentPstate, uint version)
    {
        var buffer = Marshal.AllocHGlobal(Pstates20V2Size);
        try
        {
            Span<byte> zero = stackalloc byte[Pstates20V2Size];
            Marshal.Copy(zero.ToArray(), 0, buffer, Pstates20V2Size);
            Marshal.WriteInt32(buffer, unchecked((int)version));

            try
            {
                if (bindings.GetPstates20 is null
                    || bindings.GetPstates20(gpuHandle, buffer) != NvapiOk)
                {
                    return null;
                }
            }
            catch (AccessViolationException)
            {
                return null;
            }

            var numPstates = ClampStructCount(ReadUInt32(buffer, 8), MaxPstates20Entries);
            var numBaseVoltages = ClampStructCount(ReadUInt32(buffer, 16), MaxPstates20BaseVoltages);
            if (numPstates == 0 || numBaseVoltages == 0)
            {
                return null;
            }

            var preferredVoltage = currentPstate is null
                ? null
                : TryReadVoltageFromPstate(buffer, numPstates, numBaseVoltages, currentPstate.Value);
            return preferredVoltage ?? TryReadFirstVoltage(buffer, numPstates, numBaseVoltages);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static double? TryReadVoltageFromPstate(
        IntPtr buffer,
        int numPstates,
        int numBaseVoltages,
        uint pstateId)
    {
        for (var index = 0; index < numPstates; index++)
        {
            var pstateOffset = Pstate20PstatesOffset + (index * Pstate20EntrySize);
            if (ReadUInt32(buffer, pstateOffset + Pstate20EntryIdOffset) != pstateId)
            {
                continue;
            }

            return TryReadCoreVoltage(buffer, pstateOffset, numBaseVoltages);
        }

        return null;
    }

    private static double? TryReadFirstVoltage(
        IntPtr buffer,
        int numPstates,
        int numBaseVoltages)
    {
        for (var index = 0; index < numPstates; index++)
        {
            var pstateOffset = Pstate20PstatesOffset + (index * Pstate20EntrySize);
            var voltage = TryReadCoreVoltage(buffer, pstateOffset, numBaseVoltages);
            if (voltage is not null)
            {
                return voltage;
            }
        }

        return null;
    }

    private static double? TryReadCoreVoltage(
        IntPtr buffer,
        int pstateOffset,
        int numBaseVoltages)
    {
        for (var index = 0; index < numBaseVoltages; index++)
        {
            var entryOffset = pstateOffset + Pstate20BaseVoltagesOffset + (index * BaseVoltageEntrySize);
            var domainId = ReadUInt32(buffer, entryOffset + BaseVoltageDomainOffset);
            var rawMicrovolts = ReadUInt32(buffer, entryOffset + BaseVoltageMicrovoltsOffset);
            if (domainId != 0 || rawMicrovolts == 0)
            {
                continue;
            }

            var volts = NormalizeVoltageVolts(rawMicrovolts);
            if (volts is not null)
            {
                return volts;
            }
        }

        return null;
    }

    internal static double? NormalizeVoltageVolts(uint rawVoltage)
    {
        return NormalizeVoltageVolts((ulong)rawVoltage);
    }

    internal static double? NormalizeVoltageVolts(ulong rawVoltage)
    {
        var volts = rawVoltage >= 100_000
            ? rawVoltage / 1_000_000d
            : rawVoltage >= 100
                ? rawVoltage / 1000d
                : 0;
        return volts is >= 0.4 and <= 2.0 ? volts : null;
    }

    private static uint MakeNvapiVersion(int size, int version)
    {
        return unchecked((uint)(size | (version << 16)));
    }

    private static uint ReadUInt32(IntPtr buffer, int offset)
    {
        return unchecked((uint)Marshal.ReadInt32(buffer, offset));
    }

    private static IntPtr AllocateZeroedBuffer(int size)
    {
        var buffer = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, buffer, size);
        return buffer;
    }

    private static int ClampStructCount(uint value, int max)
    {
        return value > max ? max : (int)value;
    }

    private static bool HasAnySensorValue(GpuSensorMetrics sensors)
    {
        return sensors.FanSpeedRpm is not null
            || sensors.CoreVoltageVolts is not null
            || sensors.CurrentAmps is not null;
    }
}

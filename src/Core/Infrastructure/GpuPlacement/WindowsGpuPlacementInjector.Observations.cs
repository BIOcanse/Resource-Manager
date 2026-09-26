using System.Buffers.Binary;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    internal const int DeviceObservationBufferSize = 128;
    private const uint DeviceObservationVersion = 3;

    private static async Task<GpuDeviceObservationReadResult> ReadObservationsAsync(ProviderProcess owner, CancellationToken token)
    {
        var address = owner.ReadObservationsAddress;
        if (address == IntPtr.Zero) return new(null, "observation-export-missing");
        var buffer = new byte[DeviceObservationBufferSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, DeviceObservationBufferSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), DeviceObservationVersion);
        var call = await owner.InvokeAsync(GpuRemoteCallKind.ReadDevices, address, buffer, true, token).ConfigureAwait(false);
        if (!call.Completed) return new(null, call.Status, call.NativeError);
        if (call.ExitCode != 1) return new(null, "observation-copy-failed");
        return DecodeDeviceObservations(call.Response ?? []);
    }

    internal static GpuDeviceObservationReadResult DecodeDeviceObservations(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length != DeviceObservationBufferSize || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != DeviceObservationBufferSize
            || BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]) != DeviceObservationVersion)
            return new(null, "observation-layout-invalid");

        var values = new GpuDeviceObservation[5];
        for (var index = 0; index < values.Length; index++)
        {
            var entry = buffer.Slice(8 + index * 24, 24);
            var count = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var luid = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var kind = (GpuDeviceAdapterIdentityKind)BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            if (kind > GpuDeviceAdapterIdentityKind.MultipleAdapters || BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]) != 0
                || (count == 0) != (kind == GpuDeviceAdapterIdentityKind.NoDevice)
                || (kind != GpuDeviceAdapterIdentityKind.Adapter && luid != 0))
                return new(null, "observation-value-invalid");
            values[index] = new(count, luid, kind);
        }
        return new(new(values[0], values[1], values[2], values[3], values[4]), "read");
    }
}

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class NvidiaNvapiReader
{
    private NvapiPciIdentifiers? ReadPciIdentifiers(IntPtr gpuHandle)
    {
        if (bindings.GetPciIdentifiers is null)
        {
            return null;
        }

        try
        {
            return bindings.GetPciIdentifiers(
                gpuHandle,
                out var deviceId,
                out var subSystemId,
                out var revisionId,
                out var externalDeviceId) == NvapiOk
                    ? new NvapiPciIdentifiers(deviceId, subSystemId, revisionId, externalDeviceId)
                    : null;
        }
        catch (AccessViolationException)
        {
            return null;
        }
    }

    private NvapiPciLocation? ReadPciLocation(IntPtr gpuHandle)
    {
        if (bindings.GetBusId is null
            || bindings.GetBusSlotId is null)
        {
            return null;
        }

        try
        {
            return bindings.GetBusId(gpuHandle, out var busId) == NvapiOk
                && bindings.GetBusSlotId(gpuHandle, out var busSlotId) == NvapiOk
                    ? new NvapiPciLocation(busId, busSlotId)
                    : null;
        }
        catch (AccessViolationException)
        {
            return null;
        }
    }

    private readonly record struct NvapiPciIdentifiers(
        uint DeviceId,
        uint SubSystemId,
        uint RevisionId,
        uint ExternalDeviceId);

    private readonly record struct NvapiPciLocation(
        uint BusId,
        uint BusSlotId);
}

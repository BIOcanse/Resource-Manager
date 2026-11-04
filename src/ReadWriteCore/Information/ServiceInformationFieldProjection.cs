using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Services;

namespace ResourceManager.ReadWrite.Information;

public static class ServiceInformationFieldProjection
{
    public static DeviceInformationSnapshot Create(IReadOnlyList<WindowsServiceRecord> records, DateTimeOffset observedAt)
        => new(observedAt, records.Select(record => new DeviceInformationObject(
            "windows-service|" + record.ServiceName.ToLowerInvariant(), "scm_service",
            [Integer(DeviceInformationFieldRegistry.ServiceAllStateActual, record.State is >= 1 and <= 7 ? record.State : null),
             Integer(DeviceInformationFieldRegistry.ServiceAllProcessPidActual, record.ProcessId),
             Integer(DeviceInformationFieldRegistry.ServiceAllStartTypeConfigured, record.StartType is <= 4 ? record.StartType : null)])).ToArray());
    private static DeviceInformationField Integer(DeviceInformationDefinition definition, uint? value)
        => new(definition, value is { } number ? JsonSerializer.SerializeToElement(number.ToString(CultureInfo.InvariantCulture)) : null);
}


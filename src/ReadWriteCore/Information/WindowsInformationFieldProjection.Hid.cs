using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static DeviceInformationSnapshot Hid(IReadOnlyList<DeviceTopologyHidInformation>? collections, DateTimeOffset? observedAt)
    {
        var devices = new List<DeviceInformationObject>();
        AppendHidDeclarations(devices, collections, observedAt);
        return new(observedAt, devices.AsReadOnly());
    }

    internal static void AppendHidDeclarations(List<DeviceInformationObject> devices,
        IReadOnlyList<DeviceTopologyHidInformation>? collections, DateTimeOffset? observedAt)
    {
        foreach (var hid in collections ?? [])
        {
            if (string.IsNullOrWhiteSpace(hid.DeviceIdentity)) continue;
            DeviceInformationField[] fields = [Text(F.HidAllPnpInstanceIdDeclared, hid.PnpInstanceId),
                Value(F.HidAllUsageDeclared, hid.Usage), Value(F.HidAllReportLengthDeclared, hid.ReportLengths),
                Value(F.HidAllButtonsCapability, hid.Buttons), Value(F.HidAllValueCapability, hid.Values)];
            if (observedAt is null) fields = fields.Select(static field => field with { Value = null }).ToArray();
            devices.Add(new(hid.DeviceIdentity, "hid_collection", Array.AsReadOnly(fields)));
        }
    }
}

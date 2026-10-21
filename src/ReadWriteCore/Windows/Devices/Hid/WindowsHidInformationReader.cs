using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Hid;

internal sealed record WindowsHidCollection(string DevicePath, string PnpInstanceId);

internal interface IWindowsHidCollectionApi
{
    IReadOnlyList<WindowsHidCollection>? Enumerate();
    uint? ReadVersion();
    IWindowsHidCollectionSession? Open(WindowsHidCollection collection);
}

internal interface IWindowsHidCollectionSession : IDisposable
{
    int ReadCaps(out WindowsHidCaps caps);
    int ReadButtons(WindowsHidReportType report, WindowsHidButtonCaps[] caps, ref ushort count);
    int ReadValues(WindowsHidReportType report, WindowsHidValueCaps[] caps, ref ushort count);
}

public sealed class WindowsHidInformationReader
{
    internal const int HidStatusSuccess = 0x00110000;
    internal const int MaximumCollections = 256;
    internal const int MaximumCapabilitiesPerField = 2048;
    internal const int MaximumCapabilitiesPerCapture = 16384;
    private static readonly WindowsHidReportType[] Reports = [WindowsHidReportType.Input, WindowsHidReportType.Output, WindowsHidReportType.Feature];
    private readonly IWindowsHidCollectionApi api;

    public WindowsHidInformationReader() : this(new WindowsHidNativeApi()) { }
    internal WindowsHidInformationReader(IWindowsHidCollectionApi api) => this.api = api;

    public DeviceInformationSnapshot ReadFields()
    {
        var collections = Read();
        return WindowsInformationFieldProjection.Hid(collections,
            collections is null ? null : DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<DeviceTopologyHidInformation>? Read()
    {
        IReadOnlyList<WindowsHidCollection>? collections;
        try { collections = api.Enumerate(); }
        catch (Exception) { return null; }
        if (collections is null || collections.Count > MaximumCollections) return null;
        var known = new Dictionary<string, WindowsHidCollection>(StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            if (string.IsNullOrWhiteSpace(collection.DevicePath) || string.IsNullOrWhiteSpace(collection.PnpInstanceId)
                || collection.DevicePath.Length > 8192 || collection.DevicePath.Any(char.IsControl)
                || collection.PnpInstanceId.Any(char.IsControl)) return null;
            var path = collection.DevicePath.Trim().ToUpperInvariant();
            if (!known.TryAdd("windows-hid-collection|" + path, collection with {
                DevicePath = collection.DevicePath.Trim(), PnpInstanceId = collection.PnpInstanceId.Trim().ToUpperInvariant() })) return null;
        }
        uint? version = null;
        if (known.Count > 0)
        {
            try { version = api.ReadVersion(); }
            catch (Exception) { }
        }
        var budget = MaximumCapabilitiesPerCapture;
        var results = new List<DeviceTopologyHidInformation>(known.Count);
        foreach (var (identity, collection) in known)
        {
            var information = new DeviceTopologyHidInformation(identity, collection.PnpInstanceId, null, null, null, null);
            try
            {
                using var session = api.Open(collection);
                if (session is not null && session.ReadCaps(out var caps) == HidStatusSuccess)
                {
                    information = information with {
                        Usage = new(caps.UsagePage, caps.Usage),
                        ReportLengths = new(caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength),
                        Buttons = ReadButtons(session, caps, version, ref budget),
                        Values = ReadValues(session, caps, ref budget) };
                }
            }
            catch (Exception) { }
            results.Add(information);
        }
        return results.AsReadOnly();
    }

    private static IReadOnlyList<HidButtonCapability>? ReadButtons(IWindowsHidCollectionSession session,
        WindowsHidCaps header, uint? version, ref int budget)
    {
        var total = Reports.Sum(report => (int)header.ButtonCount(report));
        if (total > MaximumCapabilitiesPerField || total > budget) return null;
        budget -= total;
        var values = new List<HidButtonCapability>(total);
        try
        {
            foreach (var report in Reports)
            {
                var expected = header.ButtonCount(report);
                if (expected == 0) continue;
                var buffer = new WindowsHidButtonCaps[expected]; var count = expected;
                if (session.ReadButtons(report, buffer, ref count) != HidStatusSuccess || count != expected) return null;
                foreach (var caps in buffer)
                    values.Add(new(Control(report, caps.UsagePage, caps.ReportId, caps.IsAlias, caps.BitField, caps.LinkCollection,
                        caps.LinkUsagePage, caps.LinkUsage, caps.IsStringRange, caps.IsDesignatorRange, caps.IsAbsolute,
                        caps.IsRange, caps.Selection), version is >= 2 ? caps.ReportCount : null));
            }
        }
        catch (Exception) { return null; }
        return values.AsReadOnly();
    }

    private static IReadOnlyList<HidValueCapability>? ReadValues(IWindowsHidCollectionSession session,
        WindowsHidCaps header, ref int budget)
    {
        var total = Reports.Sum(report => (int)header.ValueCount(report));
        if (total > MaximumCapabilitiesPerField || total > budget) return null;
        budget -= total;
        var values = new List<HidValueCapability>(total);
        try
        {
            foreach (var report in Reports)
            {
                var expected = header.ValueCount(report);
                if (expected == 0) continue;
                var buffer = new WindowsHidValueCaps[expected]; var count = expected;
                if (session.ReadValues(report, buffer, ref count) != HidStatusSuccess || count != expected) return null;
                foreach (var caps in buffer)
                    values.Add(new(Control(report, caps.UsagePage, caps.ReportId, caps.IsAlias, caps.BitField, caps.LinkCollection,
                        caps.LinkUsagePage, caps.LinkUsage, caps.IsStringRange, caps.IsDesignatorRange, caps.IsAbsolute,
                        caps.IsRange, caps.Selection), caps.HasNull != 0, caps.BitSize, caps.ReportCount, caps.UnitsExponentRaw,
                        caps.UnitsRaw, caps.LogicalMinimum, caps.LogicalMaximum, caps.PhysicalMinimum, caps.PhysicalMaximum));
            }
        }
        catch (Exception) { return null; }
        return values.AsReadOnly();
    }

    private static HidControlCapability Control(WindowsHidReportType report, ushort page, byte id, byte alias,
        ushort bitField, ushort linkCollection, ushort linkPage, ushort linkUsage, byte stringRange, byte designatorRange,
        byte absolute, byte range, WindowsHidSelection selection)
    {
        var isRange = range != 0;
        if (isRange && (selection.UsageMinimum > selection.UsageMaximum || selection.DataIndexMinimum > selection.DataIndexMaximum))
            throw new InvalidDataException("Invalid HID capability range.");
        return new(report switch { WindowsHidReportType.Input => "input", WindowsHidReportType.Output => "output",
            WindowsHidReportType.Feature => "feature", _ => throw new ArgumentOutOfRangeException(nameof(report)) },
            page, id, alias != 0, bitField, linkCollection, linkPage, linkUsage, stringRange != 0, designatorRange != 0,
            absolute != 0, new(isRange, selection.UsageMinimum, isRange ? selection.UsageMaximum : selection.UsageMinimum,
                selection.StringMinimum, isRange ? selection.StringMaximum : selection.StringMinimum,
                selection.DesignatorMinimum, isRange ? selection.DesignatorMaximum : selection.DesignatorMinimum,
                selection.DataIndexMinimum, isRange ? selection.DataIndexMaximum : selection.DataIndexMinimum));
    }
}

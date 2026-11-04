using System.Buffers.Binary;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsWlanSession : IDisposable
{
    byte[] ReadInterfaces();
    byte[] ReadCapability(Guid interfaceGuid);
    byte[] ReadCurrentConnection(Guid interfaceGuid);
}

public sealed record WindowsWlanInformationSnapshot(
    IReadOnlyDictionary<Guid, DeviceTopologyWirelessConnection>? Interfaces,
    string? Error);

/// <summary>One explicit capture; no scan, notifications, cached fallback or periodic work.</summary>
public sealed class WindowsWlanInformationReader
{
    private readonly Func<IWindowsWlanSession> open;
    public WindowsWlanInformationReader() : this(WindowsWlanSession.Open) { }
    internal WindowsWlanInformationReader(Func<IWindowsWlanSession> open) => this.open = open;

    public WindowsWlanInformationSnapshot Read()
    {
        var errors = new List<string>();
        try
        {
            using var session = open();
            var directory = DecodeInterfaces(session.ReadInterfaces());
            var values = new Dictionary<Guid, DeviceTopologyWirelessConnection>();
            foreach (var (guid, state) in directory)
            {
                IReadOnlyList<uint>? supported = null;
                uint? negotiated = null;
                try { supported = DecodeCapability(session.ReadCapability(guid)); }
                catch (Exception ex) { errors.Add($"WlanGetInterfaceCapability/{guid:D}: {ex.Message}"); }
                if (state is 1 or 2)
                {
                    try { negotiated = DecodeCurrentConnection(session.ReadCurrentConnection(guid)); }
                    catch (Exception ex) { errors.Add($"WlanQueryInterface/{guid:D}: {ex.Message}"); }
                }
                var standards = supported?.Select(Standard).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (supported?.Count > 0 && standards?.Length == 0) standards = null;
                values.Add(guid, new DeviceTopologyWirelessConnection("wlan-api", state, supported, standards,
                    negotiated, negotiated is { } phy ? Standard(phy) : null));
            }
            return new(values, errors.Count == 0 ? null : string.Join("; ", errors));
        }
        catch (Exception ex) { return new(null, "WLAN: " + ex.Message); }
    }

    internal static IReadOnlyList<(Guid Guid, uint State)> DecodeInterfaces(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("WLAN_INTERFACE_INFO_LIST header truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 1024 || data.Length != 8 + (long)count * 532)
            throw new InvalidDataException("WLAN_INTERFACE_INFO_LIST count/length invalid.");
        var rows = new List<(Guid, uint)>((int)count);
        var seen = new HashSet<Guid>();
        for (var index = 0; index < count; index++)
        {
            var row = data.Slice(8 + index * 532, 532);
            var guid = new Guid(row[..16]);
            if (guid == Guid.Empty || !seen.Add(guid)) throw new InvalidDataException("WLAN interface identity invalid or duplicated.");
            rows.Add((guid, BinaryPrimitives.ReadUInt32LittleEndian(row[528..])));
        }
        return rows;
    }

    internal static IReadOnlyList<uint> DecodeCapability(ReadOnlySpan<byte> data)
    {
        // WLAN_INTERFACE_CAPABILITY has five DWORDs followed by DOT11_PHY_TYPE[64].
        if (data.Length != 276) throw new InvalidDataException("WLAN_INTERFACE_CAPABILITY length invalid.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (count > 64) throw new InvalidDataException("WLAN capability PHY count exceeds WLAN_MAX_PHY_INDEX.");
        var values = new HashSet<uint>();
        for (var index = 0; index < count; index++)
            values.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[(20 + index * 4)..]));
        return values.Order().ToArray();
    }

    internal static uint? DecodeCurrentConnection(ReadOnlySpan<byte> data)
    {
        // WCHAR profile[256], DOT11_SSID, BSS type and six-byte BSSID precede the aligned PHY DWORD.
        if (data.Length != 604) throw new InvalidDataException("WLAN_CONNECTION_ATTRIBUTES length invalid.");
        var state = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (state is not (1 or 2)) return null;
        var phy = BinaryPrimitives.ReadUInt32LittleEndian(data[568..]);
        return phy == 0 ? null : phy;
    }

    internal static string? Standard(uint phy) => phy switch
    {
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "802.11n",
        8 => "802.11ac", 9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be",
        _ => null
    };
}

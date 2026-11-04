using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

internal static class WindowsWpdInformationIdentity
{
    internal const string DeviceObjectId = "DEVICE";
    internal const string DevicePrefix = "windows-wpd-device|";
    internal const string StoragePrefix = "windows-wpd-storage|";
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    internal static string Device(string deviceConnectionId)
        => DevicePrefix + Encode(deviceConnectionId) + "|" + Encode(DeviceObjectId);

    internal static string Storage(string deviceConnectionId, string objectId)
        => StoragePrefix + Encode(deviceConnectionId) + "|" + Encode(objectId);

    internal static Parsed Parse(string identity, int maximumStringCharacters)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string prefix;
        WindowsWpdInformationObjectKind kind;
        if (identity.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            prefix = DevicePrefix;
            kind = WindowsWpdInformationObjectKind.Device;
        }
        else if (identity.StartsWith(StoragePrefix, StringComparison.Ordinal))
        {
            prefix = StoragePrefix;
            kind = WindowsWpdInformationObjectKind.Storage;
        }
        else throw new ArgumentException("The field reference is not a WPD object identity.", nameof(identity));

        var parts = identity[prefix.Length..].Split('|');
        if (parts.Length != 2) throw new ArgumentException("The WPD object identity is malformed.", nameof(identity));
        var connectionId = Decode(parts[0]);
        var objectId = Decode(parts[1]);
        if (connectionId.Length == 0 || connectionId.Length > maximumStringCharacters
            || objectId.Length == 0 || objectId.Length > maximumStringCharacters
            || kind == WindowsWpdInformationObjectKind.Device && objectId != DeviceObjectId)
            throw new ArgumentException("The WPD object identity is outside the selected scope.", nameof(identity));
        return new(connectionId, objectId, kind);
    }

    internal static string Encode(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return Convert.ToBase64String(StrictUnicode.GetBytes(value));
    }

    private static string Decode(string value)
    {
        try { return StrictUnicode.GetString(Convert.FromBase64String(value)); }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("The WPD object identity contains an invalid encoded component.", nameof(value), exception);
        }
    }

    internal readonly record struct Parsed(
        string DeviceConnectionId,
        string ObjectId,
        WindowsWpdInformationObjectKind Kind);
}

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>Original Windows Storage identity convention, shared with the product catalog and relation projection.</summary>
public static class WindowsStorageDeviceIdentity
{
    public static string Create(string? uniqueId, string? serial)
        => !string.IsNullOrWhiteSpace(uniqueId) ? "windows-storage|unique|" + uniqueId.Trim().ToUpperInvariant()
            : !string.IsNullOrWhiteSpace(serial) ? "windows-storage|serial|" + serial.Trim().ToUpperInvariant() : string.Empty;
}

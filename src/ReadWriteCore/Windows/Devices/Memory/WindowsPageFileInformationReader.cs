using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsPageFileInformationApi
{
    IReadOnlyList<DeviceTopologyPageFileInformation>? Read();
}

public sealed class WindowsPageFileInformationReader
{
    private readonly IWindowsPageFileInformationApi api;
    internal WindowsPageFileInformationReader(IWindowsPageFileInformationApi api) => this.api = api;
    internal const int MaximumFiles = 4096;
    internal const int MaximumPathCharacters = 32767;
    public WindowsPageFileInformationReader() : this(new WindowsPageFileInformationNativeApi()) { }

    public IReadOnlyList<DeviceTopologyPageFileInformation>? Read()
    {
        try { return api.Read(); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { return null; }
    }

    public Information.DeviceInformationSnapshot ReadFields()
    {
        var rows = Read();
        return Information.WindowsInformationFieldProjection.PageFiles(rows, rows is null ? null : DateTimeOffset.UtcNow);
    }
}

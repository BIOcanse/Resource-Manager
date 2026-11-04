using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Media;

namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record DeviceTopologyAudioEndpointInformation(
    string EndpointId,
    WindowsAudioDataFlow DataFlow,
    ImmutableArray<WindowsAudioDefaultRole>? DefaultRoles = null,
    DeviceTopologyWaveFormat? MixFormat = null);

public sealed record DeviceTopologyAudioDeviceInformation(
    string DeviceInstanceId,
    string? DriverVersion);

public sealed record DeviceTopologyWaveFormat(
    ushort FormatTag,
    ushort Channels,
    uint SamplesPerSecond,
    uint AverageBytesPerSecond,
    ushort BlockAlign,
    ushort BitsPerSample,
    ushort ExtraSize,
    ushort? ValidBitsPerSample,
    uint? ChannelMask,
    Guid? SubFormat,
    string ExtraDataBase64);

public sealed record DeviceTopologyRegisteredMediaType(Guid MajorType, Guid SubType);

public enum WindowsCodecRegistrationSource { MediaFoundationMft, DirectShowCategoryMoniker }

/// <summary>Version resource tied to the exact CLSID's in-process server registration in one explicit registry view.</summary>
public sealed record DeviceTopologyCodecModuleVersion(
    WindowsCodecRegistrationSource Source,
    Guid ClassId,
    WindowsCodecRegistryView RegistryView,
    string RegisteredModulePath,
    string ResolvedModulePath,
    string FileVersion,
    string? CompanyName,
    string? ProductName);

public sealed record DeviceTopologyRegisteredMftInformation(
    WindowsMftCategory Category,
    Guid ClassId,
    ImmutableArray<DeviceTopologyRegisteredMediaType>? InputTypes = null,
    ImmutableArray<DeviceTopologyRegisteredMediaType>? OutputTypes = null,
    DeviceTopologyCodecModuleVersion? Version = null);

/// <summary>A DirectShow category entry and its exact System Device Enumerator moniker identity.
/// Property-bag CLSID and passive media-type declarations are nullable when absent.</summary>
public sealed record DeviceTopologyRegisteredDirectShowInformation(
    WindowsDirectShowCategory Category,
    string MonikerDisplayName,
    Guid? ClassId,
    string? FriendlyName = null,
    string? DevicePath = null,
    ImmutableArray<DeviceTopologyRegisteredMediaType>? InputTypes = null,
    ImmutableArray<DeviceTopologyRegisteredMediaType>? OutputTypes = null,
    DeviceTopologyCodecModuleVersion? Version = null);

/// <summary>A successful or failed media directory read. Failed reads carry no partial objects.</summary>
public sealed record WindowsMediaDirectoryRead(
    string IdentityPrefix,
    bool DirectorySucceeded,
    IReadOnlyList<DeviceInformationObject> Objects);

/// <summary>The directories observed by one explicit scoped media read.</summary>
public sealed record WindowsMediaInformationReadResult(
    IReadOnlyList<WindowsMediaDirectoryRead> Directories);

/// <summary>Caller-owned local source for explicit scoped audio endpoint, registered MFT, and
/// DirectShow category reads. Implementations return exact directory identities and only selected static fields.</summary>
public interface IWindowsMediaInformationReader
{
    WindowsMediaInformationReadResult Read(WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selectedFields, CancellationToken cancellationToken);
}

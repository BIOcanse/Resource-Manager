using ResourceManager.App.Domain.Messages;
namespace ResourceManager.App.Infrastructure.DeviceTopology;
public sealed record DeviceTopologyOemDisplayConnectorProfile(
    string Id,
    /// <summary>接口的技术名，只给原生事实缓存与匹配用，不直接显示。</summary>
    string CatalogName,
    BackendMessage DisplayName,
    string ConnectorKind,
    BackendMessage HardwareKind,
    string Protocol,
    string PhysicalMaximumSpeed,
    int OutputTechnology,
    string? PreferredAdapterHardwareIdToken,
    bool AllowAnyActiveAdapter);

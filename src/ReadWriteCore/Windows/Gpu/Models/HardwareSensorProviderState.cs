namespace ResourceManager.App.Domain.Metrics;

public static class HardwareSensorProviderStates
{
    public const string Active = "active";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
    public const string NotRequested = "not_requested";
    public const string Missing = "missing";
    public const string RuntimeAvailable = "runtime_available";
    public const string InstalledUnverified = "installed_unverified";
    public const string UnsupportedFirmware = "unsupported_firmware";
    public const string Current = "current";
}

public sealed record HardwareSensorProviderState(
    string Provider,
    string State,
    string? Message);


namespace ResourceManager.App.Infrastructure.ProcessAttribution;

public sealed class RuntimeProcessAttributionCatalogAvailabilityOptions
{
    public const string SectionName = "RuntimeProcessAttributionCatalogAvailability";

    /// <summary>Seconds between resource checks after a transient failure; zero disables checks.</summary>
    public int RecoveryCheckIntervalSeconds { get; set; } = 30;
}

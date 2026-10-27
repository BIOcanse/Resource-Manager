using ResourceManager.ReadWrite;

namespace ResourceManager.App.Application.ProcessAttribution;

/// <summary>An immutable resolver uses its own lifetime as the input version.
/// A mutable resolver publishes a new version when its external input changes.
/// Reading the version never re-observes a process.</summary>
public interface IRuntimeAttributionInputSource
{
    [RuntimeRead(ReadVolatilityKind.EventDriven, "Resolver input snapshot published")]
    object InputVersion => this;

    /// <summary>Explicit batch acquisition. Sampled external owners may advance
    /// their snapshot here; reading InputVersion itself never acquires a source.</summary>
    object CaptureInputVersion() => InputVersion;
}

internal sealed record RuntimeAttributionInputVersions(object Package, object Service, object Root, object System);


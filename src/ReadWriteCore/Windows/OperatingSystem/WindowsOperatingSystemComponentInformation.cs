namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Explicit bounds for one online-image DISM inventory. Constructing this
/// scope performs no DISM initialization or image query. DISM exposes no query-timeout
/// parameter; cancellation can stop between native calls, not a blocked call.</summary>
public sealed record WindowsOperatingSystemComponentInformationScope
{
    private const WindowsOperatingSystemInformationFields ComponentFields =
        WindowsOperatingSystemInformationFields.Features
        | WindowsOperatingSystemInformationFields.ComponentPackages
        | WindowsOperatingSystemInformationFields.DriverPackages;

    /// <summary>The scope is always the running Windows installation opened as DISM_ONLINE_IMAGE.</summary>
    public string ImageScope => "online";
    public WindowsOperatingSystemInformationFields Fields { get; }
    public int MaximumFeatures { get; }
    public int MaximumPackages { get; }
    public int MaximumDriverPackages { get; }
    public int MaximumStringCharacters { get; }
    public int MaximumCharactersPerCategory { get; }

    public WindowsOperatingSystemComponentInformationScope(
        WindowsOperatingSystemInformationFields fields = WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages,
        int maximumFeatures = 4096,
        int maximumPackages = 8192,
        int maximumDriverPackages = 8192,
        int maximumStringCharacters = 4096,
        int maximumCharactersPerCategory = 1_048_576)
    {
        if (fields == WindowsOperatingSystemInformationFields.None || (fields & ~ComponentFields) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (maximumFeatures is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(maximumFeatures));
        if (maximumPackages is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(maximumPackages));
        if (maximumDriverPackages is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(maximumDriverPackages));
        if (maximumStringCharacters is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(maximumStringCharacters));
        if (maximumCharactersPerCategory is < 1 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(maximumCharactersPerCategory));
        Fields = fields;
        MaximumFeatures = maximumFeatures;
        MaximumPackages = maximumPackages;
        MaximumDriverPackages = maximumDriverPackages;
        MaximumStringCharacters = maximumStringCharacters;
        MaximumCharactersPerCategory = maximumCharactersPerCategory;
    }
}

public sealed record WindowsOperatingSystemFeatureState(string FeatureName, uint State);
public sealed record WindowsOperatingSystemPackageState(string PackageName, uint State);
public sealed record WindowsOperatingSystemDriverPackageIdentity(string PublishedName);

/// <summary>A null category denotes a failed, unsupported or unauthorized query;
/// an empty category denotes a successful empty enumeration.</summary>
public sealed record WindowsOperatingSystemComponentInformationReadResult(
    IReadOnlyList<WindowsOperatingSystemFeatureState>? Features,
    IReadOnlyList<WindowsOperatingSystemPackageState>? Packages,
    IReadOnlyList<WindowsOperatingSystemDriverPackageIdentity>? DriverPackages);

/// <summary>Caller-owned typed source boundary. Reads are made only by explicit
/// selected capture; owners do not dispose caller-supplied implementations.</summary>
public interface IWindowsOperatingSystemComponentInformationReader
{
    WindowsOperatingSystemComponentInformationReadResult Read(
        WindowsOperatingSystemComponentInformationScope scope,
        WindowsOperatingSystemInformationFields selected,
        CancellationToken cancellationToken = default);
}

using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class WindowsComponentInformationConsumer
{
    internal static async Task RunAsync()
    {
        var source = new RecordedComponentReader();
        var scope = new WindowsOperatingSystemComponentInformationScope();
        using var owner = new WindowsOperatingSystemInformationOwner(scope, source, TimeSpan.FromSeconds(1));
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty || source.Reads != 0)
            throw new InvalidDataException("OS component construction/catalog access sampled the producer.");

        var requested = WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages;
        await owner.CaptureSelectedInformationAsync(requested);
        var catalog = core.Catalog;
        var feature = RequireField(catalog, "os_feature", "os_all_feature_state_actual", "enum", "unsigned128", "windows.dism.features");
        var package = RequireField(catalog, "os_package", "os_all_package_state_actual", "enum", "unsigned128", "windows.dism.packages");
        var driver = RequireField(catalog, "driver_package", "driver_all_package_identity_declared", "text", "string", "windows.dism.driver-packages");
        if (!feature.Read.Supported || !feature.Read.Dependencies.IsEmpty || feature.Write.Supported
            || !package.Read.Supported || !package.Read.Dependencies.IsEmpty || package.Write.Supported
            || !driver.Read.Supported || !driver.Read.Dependencies.IsEmpty || driver.Write.Supported)
            throw new InvalidDataException("Component fields must be read-only and have no installation dependency.");

        var cached = core.ReadCached(feature.Field);
        if (cached.Status != FieldObservationStatus.Current || cached.Value?.Integer != "0" || source.Reads != 1)
            throw new InvalidDataException("Cached feature state did not preserve valid enum zero or sampled again.");
        var direct = (await core.CollectAsync([package.Field], default)).Single();
        if (direct.Status != FieldObservationStatus.Current || direct.Value?.Integer != "4"
            || direct != core.ReadCached(package.Field) || source.Reads != 2
            || source.LastSelected != WindowsOperatingSystemInformationFields.ComponentPackages)
            throw new InvalidDataException("Selected package collection changed the wrong category or diverged from current.");
        Console.WriteLine("ReadWriteCore package OS component fields: feature=0, package=4, driver=oem42.inf; selected read-only source; same current; no DISM fallback.");
    }

    private static FieldCapability RequireField(ReadWriteCatalog catalog, string resource, string name,
        string unit, string valueKind, string source)
    {
        var capability = catalog.Fields.Single(item => item.Definition.Name == name);
        if (catalog.Devices.Single(device => device.Identity == capability.Field.DeviceIdentity).Resource != resource
            || capability.Definition.Unit != unit || capability.Definition.ValueKind != valueKind
            || capability.ReadSource != source)
            throw new InvalidDataException($"Canonical component field mismatch for {name}.");
        return capability;
    }

    private sealed class RecordedComponentReader : IWindowsOperatingSystemComponentInformationReader
    {
        internal int Reads { get; private set; }
        internal WindowsOperatingSystemInformationFields LastSelected { get; private set; }

        public WindowsOperatingSystemComponentInformationReadResult Read(
            WindowsOperatingSystemComponentInformationScope scope,
            WindowsOperatingSystemInformationFields selected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            LastSelected = selected;
            IReadOnlyList<WindowsOperatingSystemFeatureState>? features =
                (selected & WindowsOperatingSystemInformationFields.Features) != 0 ? new[] { new WindowsOperatingSystemFeatureState("Feature.One", 0) } : null;
            IReadOnlyList<WindowsOperatingSystemPackageState>? packages =
                (selected & WindowsOperatingSystemInformationFields.ComponentPackages) != 0 ? new[] { new WindowsOperatingSystemPackageState("Package.One", 4) } : null;
            IReadOnlyList<WindowsOperatingSystemDriverPackageIdentity>? drivers =
                (selected & WindowsOperatingSystemInformationFields.DriverPackages) != 0 ? new[] { new WindowsOperatingSystemDriverPackageIdentity("oem42.inf") } : null;
            return new(features, packages, drivers);
        }
    }
}

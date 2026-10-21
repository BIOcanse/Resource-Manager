using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;

internal static class WindowsComponentInformationSubscriptionConsumer
{
    internal static async Task RunAsync()
    {
        var source = new RecordedComponentReader();
        var scope = new WindowsOperatingSystemComponentInformationScope();
        using var owner = new WindowsOperatingSystemInformationOwner(scope, source, TimeSpan.FromSeconds(1));
        var backend = owner.CreateBackend();
        var selected = WindowsOperatingSystemInformationFields.Features
            | WindowsOperatingSystemInformationFields.ComponentPackages
            | WindowsOperatingSystemInformationFields.DriverPackages;
        await owner.CaptureSelectedInformationAsync(selected);
        ImmutableArray<FieldReference> fields =
        [
            new FieldReference("windows-os-component|feature:Feature.One", "os_all_feature_state_actual"),
            new FieldReference("windows-os-component|package:Package.One", "os_all_package_state_actual"),
            new FieldReference("windows-os-component|driver-package:oem42.inf", "driver_all_package_identity_declared")
        ];

        // This package consumer uses the public role-two/borrow contract directly and
        // does not start a worker or create another information current.
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        if (settings.RoleId != 2) throw new InvalidDataException("Role-two subscription settings were not retained.");
        using var borrow = owner.BorrowSubscriptionCapture();
        if (borrow.Bind(fields).Length != fields.Length)
            throw new InvalidDataException("OS component fields did not bind to the original collection item.");
        var result = await borrow.CollectAsync(fields, new Permit(true));
        if (!result.Committed || result.Readings.Length != 3 || source.Reads != 2)
            throw new InvalidDataException("Role-two borrowed capture did not publish the three selected fields.");
        foreach (var reading in result.Readings)
            if (reading.Status != FieldObservationStatus.Current || reading != backend.ReadCached(reading.Field))
                throw new InvalidDataException("Borrowed component reading diverged from the original OS current.");
        var direct = (await backend.CollectAsync([fields[2]], default)).Single();
        if (direct.Value?.Text != "oem42.inf" || direct != backend.ReadCached(fields[2]) || source.LastSelected != WindowsOperatingSystemInformationFields.DriverPackages)
            throw new InvalidDataException("Direct collection after the borrowed subscription did not reuse the same owner.");
        Console.WriteLine("SubscriptionCore package OS component fields: public role-two borrow committed to the original current; direct collection remained available; no worker or live DISM call.");
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

    private sealed class Permit(bool active) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit)
        {
            if (!active) return false;
            commit();
            return true;
        }
    }
}

using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class OperatingSystemQueryEvidenceConsumer
{
    internal static async Task RunAsync()
    {
        var source = new RecordedComponentReader();
        var scope = new WindowsOperatingSystemComponentInformationScope(
            WindowsOperatingSystemInformationFields.Features);
        using var owner = new WindowsOperatingSystemInformationOwner(scope, source, TimeSpan.FromSeconds(1));
        var backend = owner.CreateBackend();
        var observedMasks = new List<WindowsOperatingSystemInformationFields>();

        await owner.CaptureSelectedInformationAsync(
            WindowsOperatingSystemInformationFields.Features
                | WindowsOperatingSystemInformationFields.ComponentPackages,
            sourceQueriesCompleted: observedMasks.Add);

        Require(observedMasks.SequenceEqual([WindowsOperatingSystemInformationFields.Features]),
            "query evidence is limited to the component category actually read");
        Require(source.Reads == 1 && source.LastSelected == WindowsOperatingSystemInformationFields.Features
            && source.LastScope?.Fields == WindowsOperatingSystemInformationFields.Features,
            "package category outside scope was not sent to the reader");

        var feature = backend.Catalog.Fields.Single(static capability =>
            capability.Field.FieldName == "os_all_feature_state_actual").Field;
        var initial = backend.ReadCached(feature);
        Require(initial.Status == FieldObservationStatus.Current && initial.Value?.Integer == MetricInteger.Parse("0"),
            "recorded enum zero is a current value");

        using var borrow = owner.BorrowSubscriptionCapture();
        var binding = borrow.Bind([feature]).Single();
        Require(binding.SourceKind == FieldCollectionSourceKind.SourceQuery
            && binding.CollectionItemIds.SequenceEqual([WindowsOperatingSystemInformationOwner.CollectionItemId]),
            "borrow uses the original owner collection item and query source");

        var rejected = await borrow.CollectAsync([feature], new RejectingPermit());
        var after = backend.ReadCached(feature);
        Require(!rejected.Committed && rejected.Readings.Length == 1
            && rejected.Readings[0].Status == FieldObservationStatus.Current
            && rejected.Readings[0].Value?.Integer == MetricInteger.Parse("0")
            && after.Status == FieldObservationStatus.Current && after.Value?.Integer == MetricInteger.Parse("0"),
            "rejected publication retains the original current enum zero");
        Require(rejected.QueriedFields.SequenceEqual([feature]) && source.Reads == 2
            && source.LastSelected == WindowsOperatingSystemInformationFields.Features,
            "original borrow reports only the feature query despite rejected publication");

        Console.WriteLine("[OS-QUERY-EVIDENCE-PACKAGE] recorded scope masks, enum zero, original borrow query evidence and rejected-publication current retention.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class RecordedComponentReader : IWindowsOperatingSystemComponentInformationReader
    {
        internal int Reads { get; private set; }
        internal WindowsOperatingSystemInformationFields LastSelected { get; private set; }
        internal WindowsOperatingSystemComponentInformationScope? LastScope { get; private set; }

        public WindowsOperatingSystemComponentInformationReadResult Read(
            WindowsOperatingSystemComponentInformationScope scope,
            WindowsOperatingSystemInformationFields selected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected != WindowsOperatingSystemInformationFields.Features
                || scope.Fields != WindowsOperatingSystemInformationFields.Features)
                throw new InvalidDataException("Recorded source received a component category outside its selected scope.");
            Reads++;
            LastSelected = selected;
            LastScope = scope;
            var state = Reads == 1 ? 0u : 9u;
            return new([new WindowsOperatingSystemFeatureState("Recorded.Feature", state)], null, null);
        }
    }

    private sealed class RejectingPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
}

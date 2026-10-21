using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Processes;

internal static class ProcessDetailsInformationConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        var source = new RecordedProcessDetailsSource();
        owner.ConfigureProcessDetails(WindowsProcessDetailsOptions.Default, source);
        if (owner.ReadCachedSnapshot() is not null || owner.ReadProcessDetails().Devices.Count != 0)
            throw new InvalidOperationException("Process construction unexpectedly captured detail values.");

        var backend = owner.ProcessDetailsBackend;
        var catalog = await backend.DiscoverAsync(timeout.Token);
        var definitions = new[]
        {
            DeviceInformationFieldRegistry.ProcessAllImageSignatureReported,
            DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual,
            DeviceInformationFieldRegistry.ProcessAllThreadStateActual
        };
        foreach (var definition in definitions)
        {
            var descriptor = catalog.Fields.FirstOrDefault(field => field.Definition.Name == definition.Name);
            if (descriptor is not null && (descriptor.Definition.Unit != definition.Unit
                || descriptor.Definition.ValueKind != InformationFieldProjection.Describe(definition).ValueKind
                || catalog.Devices.Single(device => device.Identity == descriptor.Field.DeviceIdentity).Resource != definition.Resource
                || descriptor.Write.Supported))
                throw new InvalidOperationException($"Process detail descriptor mismatch: {definition.Name}.");
        }

        var identityPrefix = $"windows-process|{RecordedProcessDetailsSource.Identity.ProcessId}:{RecordedProcessDetailsSource.Identity.StartKey}";
        var selected = catalog.Fields.Where(field => field.Field.DeviceIdentity.StartsWith(identityPrefix, StringComparison.Ordinal)
                && field.Definition.Name is "process_all_image_signature_reported"
                    or "process_all_loaded_module_actual" or "process_all_thread_state_actual")
            .GroupBy(static field => field.Definition.Name, StringComparer.Ordinal)
            .Select(static group => group.First().Field).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("Recorded source did not expose its exact PID/creation identity.");
        if (source.InventoryReads != 1 || source.DetailReads != 1)
            throw new InvalidOperationException("Catalog discovery did not stay on the explicitly supplied recorded source.");

        var snapshot = owner.ReadProcessDetails();
        var readings = await backend.CollectAsync([.. selected], timeout.Token);
        if (source.InventoryReads != 2 || source.DetailReads != 2)
            throw new InvalidOperationException("Direct detail collection fell back from the recorded source.");
        if (readings.Length != selected.Length) throw new InvalidOperationException("Selected process detail projection was incomplete.");
        foreach (var reading in readings)
        {
            if (backend.ReadCached(reading.Field) != reading)
                throw new InvalidOperationException("Process details did not read and publish through the owner's current.");
            if (reading.Status != FieldObservationStatus.Current && reading.Value is not null)
                throw new InvalidOperationException("Unavailable process detail returned a fabricated value.");
            Console.WriteLine($"Process detail: field={reading.Field.FieldName}; status={reading.Status}; text={reading.Value?.Text}; integer={reading.Value?.Integer}; source={reading.SourceId}.");
        }
        if (ReferenceEquals(snapshot, owner.ReadProcessDetails()))
            throw new InvalidOperationException("An explicit process detail read did not replace the selected current part.");
        if (owner.ReadCachedSnapshot() is not null)
            throw new InvalidOperationException("Typed process details were inserted into the existing 25-field numeric current.");
        Console.WriteLine("ReadWriteCore package consumer completed: recorded identity, configured bounds/source, selected typed reads, same owner current, and no native detail fallback.");
    }

    private sealed class RecordedProcessDetailsSource : IWindowsProcessDetailsCaptureSource
    {
        internal static readonly ProcessInstanceKey Identity = new(4242, 638900000000000000);
        private static readonly DateTimeOffset CapturedAt = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        private int inventoryReads;
        private int detailReads;
        internal int InventoryReads => Volatile.Read(ref inventoryReads);
        internal int DetailReads => Volatile.Read(ref detailReads);

        public WindowsProcessDetailsInventory ReadInventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref inventoryReads);
            return new(true, [Identity], CapturedAt);
        }

        public IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ReadDetails(
            IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref detailReads);
            return selection.ToDictionary(static pair => pair.Key, static pair => new WindowsProcessDetailsReadResult(
                (pair.Value & WindowsProcessDetailsFields.ImageSignature) != 0,
                (pair.Value & WindowsProcessDetailsFields.ImageSignature) != 0 ? 0u : null,
                (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0,
                (pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0
                    ? [new WindowsProcessLoadedModule(0x1000, "C:\\Recorded\\process.dll")] : [],
                (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0,
                (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0
                    ? [new WindowsProcessThreadState(77, 5, 2)] : []));
        }
    }
}

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class ProcessDetailsInformationSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        var source = new RecordedProcessDetailsSource();
        owner.ConfigureProcessDetails(WindowsProcessDetailsOptions.Default, source);
        if (owner.ReadCachedSnapshot() is not null || owner.ReadProcessDetails().Devices.Count != 0)
            throw new InvalidOperationException("Process construction unexpectedly captured detail values.");
        var backend = owner.ProcessDetailsBackend;
        var catalog = await backend.DiscoverAsync(timeout.Token);
        var prefix = $"windows-process|{RecordedProcessDetailsSource.Identity.ProcessId}:{RecordedProcessDetailsSource.Identity.StartKey}";
        var selected = catalog.Fields.Where(field => field.Field.DeviceIdentity.StartsWith(prefix, StringComparison.Ordinal)
                && field.Definition.Name is "process_all_image_signature_reported"
                    or "process_all_loaded_module_actual" or "process_all_thread_state_actual")
            .GroupBy(static field => field.Definition.Name, StringComparer.Ordinal)
            .Select(static group => group.First().Field).ToImmutableArray();
        if (selected.IsEmpty)
            throw new InvalidOperationException("Recorded source did not expose its exact PID/creation identity.");
        if (source.InventoryReads != 1 || source.DetailReads != 1)
            throw new InvalidOperationException("Catalog discovery did not stay on the explicitly supplied recorded source.");

        var borrow = owner.BorrowProcessDetailsSubscriptionCapture();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
            Generation = 1,
            MaximumSourceCount = 8,
            MaximumItemCount = 8,
            MaximumMembershipCount = 16,
            MaximumDueItemCount = 8,
            MaximumSourceViewCount = 8,
            MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000,
            MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000,
            MaximumFutureSkewMilliseconds = 1000
        };
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => Console.WriteLine($"Process details worker failure: stage={failure.Stage}; {failure.Error.Message}"),
            TimeSpan.FromMilliseconds(100));
        await composition.StartAsync(timeout.Token);
        await using (var stream = composition.SubscribeAsync("selected-process-details", selected,
            TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator())
        {
            if (!await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
                throw new InvalidDataException("Selected process details subscription ended without an observation.");
            if (source.InventoryReads <= 1 || source.DetailReads <= 1)
                throw new InvalidDataException("Role-2 sampling did not use the explicitly configured recorded source.");
            foreach (var reading in stream.Current)
            {
                FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(reading.Field));
                if (reading.Status != FieldObservationStatus.Current && reading.Value is not null)
                    throw new InvalidDataException("Unavailable process detail fabricated a value.");
                Console.WriteLine($"Process detail subscription: field={reading.Field.FieldName}; status={reading.Status}; source={reading.SourceId}; same-current=yes.");
            }
            await composition.StopAsync();
            if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired process detail stream emitted a late value.");
        }

        var inventoryReadsAtStop = source.InventoryReads;
        var readsAtStop = source.DetailReads;
        var afterStop = await backend.CollectAsync(selected, timeout.Token);
        if (afterStop.Length != selected.Length || owner.ReadCachedSnapshot() is not null)
            throw new InvalidDataException("Role-2 stop changed process owner scope or numeric process fields.");
        if (source.DetailReads <= readsAtStop || source.InventoryReads <= inventoryReadsAtStop)
            throw new InvalidDataException("Direct collection after role-2 stop did not use the configured recorded source.");
        Console.WriteLine("SubscriptionCore package consumer completed: recorded identity, shared role-2 permit/current, explicit stop, and recorded direct read after stop with no native detail fallback.");
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

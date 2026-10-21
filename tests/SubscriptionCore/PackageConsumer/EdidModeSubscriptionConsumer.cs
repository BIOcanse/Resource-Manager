using ResourceManager.TestFixtures;
using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using System.Runtime.CompilerServices;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static partial class EdidModeSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        var source = new RecordedSource(); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "construction/catalog passive");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await backend.DiscoverAsync(timeout.Token); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads;
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration {
            AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
            Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1, ActiveTtlMilliseconds = 3000,
            MaximumFutureSkewMilliseconds = 1000 };
        var failures = new List<SubscriptionWorkerFailure>();
        var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(timeout.Token);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode", [field], TimeSpan.FromMilliseconds(250), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "original role2 native composition delivers declaration");
            var reading = stream.Current.Single(); Verify(reading);
            await borrow.SecondQueryStarted.WaitAsync(timeout.Token);
            await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "original stop/drain completes without failure");
            var cached = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && cached.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && cached.ObservedAt == reading.ObservedAt, "subscription and original owner share current");
            var stoppedReads = source.Reads;
            await Task.Delay(300, timeout.Token);
            var stopped = backend.ReadCached(field);
            Require(source.Reads == stoppedReads && stopped.ObservedAt == cached.ObservedAt
                && stopped.Value!.Structure!.Value.GetRawText() == cached.Value!.Structure!.Value.GetRawText(), "stopped source count and exact current remain stable");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], timeout.Token)).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "stop leaves direct original owner usable");
        source.Fail = false; Verify((await backend.CollectAsync([field], timeout.Token)).Single());
        await RunScenarioAsync(token => RunAdvancedRecordedAsync(settings, configuration, token));
        await RunScenarioAsync(token => RunAdvancedRecordedAsync(settings, configuration, token, legacy: true));
        await RunScenarioAsync(token => RunAdvancedRecordedAsync(settings, configuration, token, originalVic: true));
        await RunScenarioAsync(token => RunAdvancedRecordedAsync(settings, configuration, token, legacy: true, originalVic: true));
        await RunScenarioAsync(token => RunAdvancedRecordedAsync(settings, configuration, token, legacy: true, originalVic: true, bitmap: true, containerRevision: 0x12));
        await RunScenarioAsync(token => RunType3RecordedAsync(settings, configuration, token));
        await RunScenarioAsync(token => RunTypeXRecordedAsync(settings, configuration, token, 0x20));
        await RunScenarioAsync(token => RunTypeXRecordedAsync(settings, configuration, token, 0x20, ctaMetadata: true));
        await RunScenarioAsync(token => RunEmbeddedSvdRecordedAsync(settings, configuration, token, 0x20));
        await RunScenarioAsync(token => RunEmbeddedColourRecordedAsync(settings, configuration, token, 0x20));
        await RunScenarioAsync(token => RunEmbeddedHdmiRecordedAsync(settings, configuration, token, 0x20));
        await RunFormulaRecordedAsync(settings, configuration, 0x20);
        await RunChromaticityRecordedAsync(settings, configuration);
        await RunScenarioAsync(token => RunCompactRecordedAsync(settings, configuration, token, false));
        await RunScenarioAsync(token => RunCompactRecordedAsync(settings, configuration, token, true));
        await RunScenarioAsync(token => RunCompactRecordedAsync(settings, configuration, token, false, mixed: true));
        await RunScenarioAsync(token => RunDynamicRangeRecordedAsync(settings, configuration, token));
        Console.WriteLine("[EDID-MODE-SUB-RECORDED] native-role2; exact-display; two-DTD; rational; same-current; stop/drain; direct-recovery; T7-exact-CTA-DSCnull; ordered-SVR-source-references; NVR-codes-unit; T8-full-DMT-code-raw-also420; T10-CVT-parameters-SVR147; DisplayID-VIII-full-DMT-raw-null-CTA-domain; DisplayID-IV-independent-legacy-byte-DMT; DisplayID-original-CTA-VIC-HDMI-VIC13-full-domain; wire12-bitmap17-raw-bitindex-code; TypeIII-independent2-width-aspect-nominal-field-frame; TypeX-independent3-CVT-wire20; nonmode-CTA-wire20-original-owner-raw-recovery; no-live-display");
    }

    private static async Task RunScenarioAsync(Func<CancellationToken, Task> scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await scenario(timeout.Token);
    }

    private static async Task RunAdvancedRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken cancellationToken, bool legacy = false, bool originalVic = false, bool bitmap = false, byte containerRevision = 0x12)
    {
        var source = new RecordedSource(advanced: true, legacy: legacy, originalVic: originalVic, bitmap: bitmap, containerRevision: containerRevision); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "advanced construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyAdvanced(backend.ReadCached(field), legacy, originalVic, bitmap, containerRevision); Require(source.Reads == reads, "advanced passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "advanced static declarations on inactive path");
        var failures = new List<SubscriptionWorkerFailure>();
        var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-selection", [field],
                TimeSpan.FromMilliseconds(250), new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "advanced original role2 delivers typed selection");
            var reading = stream.Current.Single(); VerifyAdvanced(reading, legacy, originalVic, bitmap, containerRevision);
            await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "advanced original stop/drain completes");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "advanced source queried and role2 shares original current");
            var stoppedReads = source.Reads;
            await Task.Delay(300, cancellationToken);
            var stopped = backend.ReadCached(field);
            Require(source.Reads == stoppedReads && stopped.ObservedAt == current.ObservedAt
                && stopped.Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "advanced stopped source count and exact current remain stable");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "advanced stop leaves Direct available");
        source.Fail = false; VerifyAdvanced((await backend.CollectAsync([field], cancellationToken)).Single(), legacy, originalVic, bitmap, containerRevision);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        source.SetUnknownDmt(true);
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(item => item.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(item => item.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), bitmap ? "unknown bitmap header clears all17 modes while exact640B raw remains available" : legacy ? "stopped subscription permits Direct unknown IV code0 with exact640B raw retained" : "stopped subscription permits Direct unknown0104 with exact640B raw retained");
        source.SetUnknownDmt(false);
        VerifyAdvanced((await backend.CollectAsync([field], cancellationToken)).Single(), legacy, originalVic, bitmap, containerRevision);
    }

    // Keep the original owner/permit/result. Only the second recorded query waits
    // before touching its source, until the real subscription Stop cancels it.
    private sealed class FirstQueryBorrow(IReadWriteCollectionBorrow inner) : IReadWriteCollectionBorrow
    {
        private int calls;
        private readonly TaskCompletionSource secondQueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task SecondQueryStarted => secondQueryStarted.Task;
        public IReadWriteBackend Backend => inner.Backend;
        public SamplingPublicationSignal Publications => inner.Publications;
        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields) => inner.Bind(fields);
        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields) => inner.SynchronizeDemand(activeFields);
        public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
            IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
                return await inner.CollectAsync(fields, permit, cancellationToken);
            secondQueryStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Recorded canceled query wait unexpectedly completed.");
        }
        public void Dispose() => inner.Dispose();
    }

    private static async Task RunTypeXRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken cancellationToken, byte version, bool ctaMetadata = false)
    {
        var source = new RecordedSource(typeX: true, typeXVersion: version, ctaMetadata: ctaMetadata); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "TypeX role2 construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyTypeX(backend.ReadCached(field), version); Require(source.Reads == reads, "TypeX role2 passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "TypeX role2 static inactive declaration");
        var failures = new List<SubscriptionWorkerFailure>(); var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-typex", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "TypeX original role2 delivers actual source result");
            var reading = stream.Current.Single(); VerifyTypeX(reading, version); await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync(); Require(!await stream.MoveNextAsync() && failures.Count == 0, "TypeX original role2 stop/drain cancels queued second query");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "TypeX role2 and owner current agree");
            var stoppedReads = source.Reads; await Task.Delay(300, cancellationToken);
            Require(source.Reads == stoppedReads && backend.ReadCached(field).ObservedAt == current.ObservedAt
                && backend.ReadCached(field).Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "TypeX stopped current stays exact and passive");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "TypeX stop leaves original Direct usable");
        source.Fail = false; VerifyTypeX((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetUnknownTypeX(true);
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "TypeX Direct after stop rejects unknown last descriptor and retains exact raw");
        source.SetUnknownTypeX(false); VerifyTypeX((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        if (ctaMetadata) {
            source.SetModeBearingCta(true);
            var unsupported = await backend.CollectAsync([field, rawField], cancellationToken);
            Require(unsupported.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
                && backend.ReadCached(field).Value is null && unsupported.Single(row => row.Field == rawField).Value?.Bytes is { } exact
                && exact.AsSpan().SequenceEqual(source.RawBytes), "mode-bearing embedded CTA clears all modes while complete raw survives");
            source.SetModeBearingCta(false);
            VerifyTypeX((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        }
    }

    private static async Task RunEmbeddedSvdRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken cancellationToken, byte version)
    {
        var source = new RecordedSource(embeddedSvd: true, typeXVersion: version); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "embedded-SVD role2 construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyEmbeddedSvd(backend.ReadCached(field), version); Require(source.Reads == reads, "embedded-SVD role2 passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "embedded-SVD role2 static inactive declaration");
        var failures = new List<SubscriptionWorkerFailure>(); var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-typex", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "embedded-SVD original role2 delivers actual source result");
            var reading = stream.Current.Single(); VerifyEmbeddedSvd(reading, version); await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync(); Require(!await stream.MoveNextAsync() && failures.Count == 0, "embedded-SVD original role2 stop/drain cancels queued second query");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "embedded-SVD role2 and owner current agree");
            var stoppedReads = source.Reads; await Task.Delay(300, cancellationToken);
            Require(source.Reads == stoppedReads && backend.ReadCached(field).ObservedAt == current.ObservedAt
                && backend.ReadCached(field).Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "embedded-SVD stopped current stays exact and passive");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "embedded-SVD stop leaves original Direct usable");
        source.Fail = false; VerifyEmbeddedSvd((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetUnsupportedSvd(true);
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "embedded-SVD Direct after stop rejects unsupported inner VFDB and retains exact raw");
        source.SetUnsupportedSvd(false); VerifyEmbeddedSvd((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        Console.WriteLine($"[EDID-MODE-SUB-EMBEDDED-SVD] version={version:X2}; full-VIC193; native-bit; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyEmbeddedSvd(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "embedded SVD exact original identity/current/source");
        DisplayIdCtaSvdExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static async Task RunEmbeddedColourRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken cancellationToken, byte version)
    {
        var source = new RecordedSource(embeddedColour: true, typeXVersion: version); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "embedded-colour role2 construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyEmbeddedColour(backend.ReadCached(field), version); Require(source.Reads == reads, "embedded-colour role2 passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "embedded-colour role2 static inactive declaration");
        var failures = new List<SubscriptionWorkerFailure>(); var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-typex", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "embedded-colour original role2 delivers actual source result");
            var reading = stream.Current.Single(); VerifyEmbeddedColour(reading, version); await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync(); Require(!await stream.MoveNextAsync() && failures.Count == 0, "embedded-colour original role2 stop/drain cancels queued second query");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "embedded-colour role2 and owner current agree");
            var stoppedReads = source.Reads; await Task.Delay(300, cancellationToken);
            Require(source.Reads == stoppedReads && backend.ReadCached(field).ObservedAt == current.ObservedAt
                && backend.ReadCached(field).Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "embedded-colour stopped current stays exact and passive");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "embedded-colour stop leaves original Direct usable");
        source.Fail = false; VerifyEmbeddedColour((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetInvalidColourMap(true); var expectedRaw = source.RawBytes;
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(expectedRaw), "embedded-colour Direct after stop rejects out-of-range embedded colour bitmap and retains exact raw");
        source.SetInvalidColourMap(false); VerifyEmbeddedColour((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        Console.WriteLine($"[EDID-MODE-SUB-EMBEDDED-COLOUR] version={version:X2}; full-VIC193; native-bit; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyEmbeddedColour(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "embedded colour exact original identity/current/source");
        DisplayIdCtaColourExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static async Task RunEmbeddedHdmiRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken cancellationToken, byte version)
    {
        var source = new RecordedSource(embeddedHdmi: true, typeXVersion: version); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "embedded-hdmi role2 construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyEmbeddedHdmi(backend.ReadCached(field), version); Require(source.Reads == reads, "embedded-hdmi role2 passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "embedded-hdmi role2 static inactive declaration");
        var failures = new List<SubscriptionWorkerFailure>(); var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-typex", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "embedded-hdmi original role2 delivers actual source result");
            var reading = stream.Current.Single(); VerifyEmbeddedHdmi(reading, version); await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync(); Require(!await stream.MoveNextAsync() && failures.Count == 0, "embedded-hdmi original role2 stop/drain cancels queued second query");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "embedded-hdmi role2 and owner current agree");
            var stoppedReads = source.Reads; await Task.Delay(300, cancellationToken);
            Require(source.Reads == stoppedReads && backend.ReadCached(field).ObservedAt == current.ObservedAt
                && backend.ReadCached(field).Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "embedded-hdmi stopped current stays exact and passive");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "embedded-hdmi stop leaves original Direct usable");
        source.Fail = false; VerifyEmbeddedHdmi((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetInvalidHdmiCode(true); var expectedRaw = source.RawBytes;
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(expectedRaw), "embedded-hdmi Direct after stop rejects unsupported embedded HDMI code and retains exact raw");
        source.SetInvalidHdmiCode(false); VerifyEmbeddedHdmi((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        Console.WriteLine($"[EDID-MODE-SUB-EMBEDDED-HDMI] version={version:X2}; full-VIC193; native-bit; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyEmbeddedHdmi(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "embedded HDMI exact original identity/current/source");
        DisplayIdCtaHdmiExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static async Task RunFormulaRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, byte version)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancellationToken = timeout.Token;
        var source = new RecordedSource(formula: true, typeXVersion: version); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "type-ix role2 construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyFormula(backend.ReadCached(field), version); Require(source.Reads == reads, "type-ix role2 passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "type-ix role2 static inactive declaration");
        var failures = new List<SubscriptionWorkerFailure>(); var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-formula", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "type-ix original role2 delivers actual source result");
            var reading = stream.Current.Single(); VerifyFormula(reading, version); await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync(); Require(!await stream.MoveNextAsync() && failures.Count == 0, "type-ix original role2 stop/drain cancels queued second query");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "type-ix role2 and owner current agree");
            var stoppedReads = source.Reads; await Task.Delay(300, cancellationToken);
            Require(source.Reads == stoppedReads && backend.ReadCached(field).ObservedAt == current.ObservedAt
                && backend.ReadCached(field).Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "type-ix stopped current stays exact and passive");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "type-ix stop leaves original Direct usable");
        source.Fail = false; VerifyFormula((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetInvalidFormulaOption(true); var expectedRaw = source.RawBytes;
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(expectedRaw), "type-ix Direct after stop rejects unsupported Type IX option and retains exact raw");
        source.SetInvalidFormulaOption(false); VerifyFormula((await backend.CollectAsync([field], cancellationToken)).Single(), version);
        Console.WriteLine($"[EDID-MODE-SUB-FORMULA] version={version:X2}; standard-CVT; RB1; RB2; stereo; fractional-support; actual-owner; exact-raw; recovery; no-live-display");
    }

    private static void VerifyFormula(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "Type IX formula exact original identity/current/source");
        DisplayIdFormulaExpectation.Verify(reading.Value!.Structure!.Value, version);
    }

    private static void VerifyTypeX(FieldReading reading, byte version)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "TypeX exact original owner identity/current/source");
        var value = reading.Value!.Structure!.Value; var modes = value.GetProperty("modes");
        Require(value.GetProperty("ctaFlags").ValueKind == System.Text.Json.JsonValueKind.Null, "embedded CTA does not invent extension flags");
        Require(modes.GetArrayLength() == 3 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "TypeX complete independent directory outside CTA ordinals");
        byte[][] bodies = [[0x12,0x7F,7,0x37,4,0x3B], [2,0x7F,7,0x37,4,0x2B,1], [0x9B,0x7F,7,0x37,4,0x3B,0x58,1]];
        uint[] offsets = [8,17,27];
        for (var i = 0; i < 3; i++) {
            var mode = modes[i]; var cvt = mode.GetProperty("cvtParameters");
            Require(mode.GetProperty("encoding").GetString() == "displayid-x" && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == offsets[i] && mode.GetProperty("revision").GetByte() == 0
                && mode.GetProperty("containerFlags").GetByte() == i * 16 && mode.GetProperty("containerRevision").GetByte() == version
                && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == (i == 1 ? 300u : 60u) && !mode.GetProperty("interlaced").GetBoolean()
                && mode.GetProperty("formula").GetString() == "cvt" && mode.GetProperty("reducedBlanking").GetBoolean()
                && mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(bodies[i])
                && cvt.GetProperty("reducedBlankingVersion").GetByte() == (i == 2 ? 3 : 2) && cvt.GetProperty("stereoCode").GetByte() == 0
                && mode.GetProperty("yCbCr420Encoding").GetBoolean() == (i == 2), "TypeX complete source/body/nominal/formula/container values");
            if (i < 2) {
                Require(cvt.GetProperty("alternativeRefresh1000Over1001Supported").GetBoolean() == (i == 0)
                    && mode.GetProperty("yCbCr420Supported").ValueKind == System.Text.Json.JsonValueKind.Null, "TypeX RB2 original fractional flag");
                foreach (var key in new[] { "horizontalBlankingBase160Pixels", "earlyVsyncRequired", "deltaHorizontalBlankingCode", "additionalVerticalBlankingCode", "alternativeMinimumVerticalBlanking" })
                    Require(cvt.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null, "TypeX unreported RB3 parameter " + key);
            } else Require(cvt.GetProperty("alternativeRefresh1000Over1001Supported").ValueKind == System.Text.Json.JsonValueKind.Null
                && cvt.GetProperty("horizontalBlankingBase160Pixels").GetBoolean() && cvt.GetProperty("earlyVsyncRequired").GetBoolean()
                && cvt.GetProperty("deltaHorizontalBlankingCode").GetByte() == 6 && cvt.GetProperty("additionalVerticalBlankingCode").GetByte() == 2
                && cvt.GetProperty("alternativeMinimumVerticalBlanking").GetBoolean() && mode.GetProperty("yCbCr420Supported").GetBoolean(), "TypeX actual8B RB3 parameters");
            foreach (var key in new[] { "timing", "code", "native", "preferred", "bitIndex", "horizontalPixelRepetition", "dscPassThrough", "yCbCr420Only", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null, "TypeX never invents " + key);
            Require(!mode.TryGetProperty("aspectRatioDeclared", out _), "TypeX does not inherit TypeIII aspect");
        }
    }

    private static async Task RunType3RecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken cancellationToken)
    {
        var source = new RecordedSource(type3: true); using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "TypeIII role2 construction/catalog passive");
        await backend.DiscoverAsync(cancellationToken); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var reads = source.Reads; VerifyType3(backend.ReadCached(field)); Require(source.Reads == reads, "TypeIII role2 passive current");
        Require(backend.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null, "TypeIII role2 static inactive declaration");
        var failures = new List<SubscriptionWorkerFailure>(); var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow,
            settings, in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250)))
        {
            await subscription.StartAsync(cancellationToken);
            await using var stream = subscription.SubscribeAsync("recorded-edid-mode-type3", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellationToken).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "TypeIII original role2 delivers actual source result");
            var reading = stream.Current.Single(); VerifyType3(reading); await borrow.SecondQueryStarted.WaitAsync(cancellationToken);
            await subscription.StopAsync(); Require(!await stream.MoveNextAsync() && failures.Count == 0, "TypeIII original role2 stop/drain cancels queued second query");
            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1 && current.Value!.Structure!.Value.GetRawText() == reading.Value!.Structure!.Value.GetRawText()
                && current.ObservedAt == reading.ObservedAt, "TypeIII role2 and owner current agree");
            var stoppedReads = source.Reads; await Task.Delay(300, cancellationToken);
            Require(source.Reads == stoppedReads && backend.ReadCached(field).ObservedAt == current.ObservedAt
                && backend.ReadCached(field).Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(), "TypeIII stopped current stays exact and passive");
        }
        source.Fail = true;
        Require((await backend.CollectAsync([field], cancellationToken)).Single() is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "TypeIII stop leaves original Direct usable");
        source.Fail = false; VerifyType3((await backend.CollectAsync([field], cancellationToken)).Single());
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared"); source.SetUnknownType3(true);
        var unknown = await backend.CollectAsync([field, rawField], cancellationToken);
        Require(unknown.Single(row => row.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null && unknown.Single(row => row.Field == rawField).Value?.Bytes is { } raw
            && raw.AsSpan().SequenceEqual(source.RawBytes), "TypeIII Direct after stop rejects unknown last descriptor and retains exact raw");
        source.SetUnknownType3(false); VerifyType3((await backend.CollectAsync([field], cancellationToken)).Single());
    }

    private static void VerifyType3(FieldReading reading)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null && reading.Value?.Structure is not null, "TypeIII exact original display identity/current/source");
        var value = reading.Value!.Structure!.Value; var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 2 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "TypeIII independent complete two declarations");
        for (var i = 0; i < 2; i++)
        {
            var mode = modes[i]; var aspect = mode.GetProperty("aspectRatioDeclared"); var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == "displayid-iii" && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == (i == 0 ? 8u : 11u) && mode.GetProperty("revision").GetByte() == 1
                && mode.GetProperty("containerFlags").GetByte() == 1 && mode.GetProperty("containerRevision").GetByte() == 0x12
                && mode.GetProperty("width").GetUInt32() == (i == 0 ? 1920u : 8u)
                && mode.GetProperty("interlaced").GetBoolean() == (i == 0)
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == (i == 0 ? 60u : 1u)
                && !mode.GetProperty("preferred").GetBoolean() && mode.GetProperty("formula").GetString() == "cvt"
                && mode.GetProperty("reducedBlanking").GetBoolean() == (i == 0)
                && raw.EnumerateArray().Select(item => item.GetByte()).SequenceEqual(i == 0 ? new byte[] { 20, 239, 187 } : new byte[] { 8, 0, 0 })
                && aspect.GetProperty("code").GetByte() == (i == 0 ? 4 : 8), "TypeIII width/interlace/preferred/nominal/raw/source declarations");
            Require(i == 0 ? aspect.GetProperty("horizontal").GetUInt32() == 16 && aspect.GetProperty("vertical").GetUInt32() == 9
                : aspect.GetProperty("horizontal").ValueKind == System.Text.Json.JsonValueKind.Null && aspect.GetProperty("vertical").ValueKind == System.Text.Json.JsonValueKind.Null,
                "TypeIII exact aspect or explicitly undefined original code");
            foreach (var key in new[] { "height", "timing", "code", "native", "bitIndex", "dscPassThrough", "cvtParameters", "yCbCr420Encoding", "yCbCr420Supported", "yCbCr420Only", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null, "TypeIII never generates unreported " + key);
        }
    }

    private sealed partial class RecordedSource : IDeviceTopologyReader
    {
        internal const string Identity = "recorded-display-path|monitor-1|connector-7";
        internal const string Name = "display_all_mode_capability";
        private readonly byte[] bytes;
        private readonly bool legacy;
        private readonly bool originalVic;
        private readonly bool bitmap;
        internal RecordedSource(bool advanced = false, bool legacy = false, bool originalVic = false, bool bitmap = false, byte containerRevision = 0x12, bool type3 = false, bool typeX = false, byte typeXVersion = 0x20, bool ctaMetadata = false, bool embeddedSvd = false, bool embeddedColour = false, bool embeddedHdmi = false, bool formula = false)
        {
            this.legacy = legacy; this.originalVic = originalVic; this.bitmap = bitmap;
            bytes = formula ? DisplayIdFormulaFixture.Bytes(typeXVersion) : embeddedHdmi ? DisplayIdCtaHdmiFixture.Bytes(typeXVersion) : embeddedColour ? DisplayIdCtaColourFixture.Bytes(typeXVersion) : embeddedSvd ? DisplayIdCtaSvdFixture.Bytes(typeXVersion) : ctaMetadata ? DisplayIdNonModeCtaFixture.Bytes(typeXVersion) : typeX ? Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIB4CACoABhJ/BzcEOyoQBwJ/BzcEKwEqIAibfwc3BDtYARAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==") : type3 ? Convert.FromBase64String("AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwEgkCAAUBBhTvuwgAABEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==") : advanced ? AdvancedBytes(legacy, originalVic, bitmap, containerRevision) : Bytes();
            if (typeX) { bytes[129] = typeXVersion; RecheckTypeX(); }
        }
        private DeviceTopologyDisplayPath? captured;
        internal int Reads { get; private set; }
        internal bool Fail { get; set; }
        internal byte[] RawBytes => bytes.ToArray();
        internal void SetUnknownTypeX(bool unknown)
        {
            Require(bytes.Length == 256 && bytes[133] == 0x2A, "only independent TypeX input has this body layout");
            bytes[155] = unknown ? (byte)0 : (byte)0x9B; RecheckTypeX();
        }
        internal void SetModeBearingCta(bool modeBearing) => DisplayIdNonModeCtaFixture.SetModeBearingCta(bytes, modeBearing);
        internal void SetUnsupportedSvd(bool unsupported) => DisplayIdCtaSvdFixture.SetUnsupportedCta(bytes, unsupported);
        internal void SetInvalidColourMap(bool invalid)
        {
            Require(bytes.Length == 256 && bytes[130] == 22 && bytes[142] == 0x81, "independent embedded colour literal");
            bytes[154] = invalid ? (byte)8 : (byte)5; RecheckTypeX();
        }
        internal void SetInvalidHdmiCode(bool invalid)
        {
            Require(bytes.Length == 256 && bytes[130] == 37 && bytes[142] == 0x81, "independent embedded HDMI literal");
            bytes[169] = invalid ? (byte)5 : (byte)4; RecheckTypeX();
        }
        internal void SetInvalidFormulaOption(bool invalid)
        {
            Require(bytes.Length == 256 && bytes[133] == 0x24, "independent Type IX literal");
            bytes[148] = invalid ? (byte)3 : (byte)0x52;
            DisplayIdFormulaFixture.Repair(bytes);
        }
        private void RecheckTypeX()
        {
            var end = checked(133 + bytes[130]); bytes[end] = 0; var sum = 0;
            for (var i = 129; i < end; i++) sum += bytes[i]; bytes[end] = unchecked((byte)-sum);
            bytes[255] = 0; sum = 0;
            for (var i = 128; i < 255; i++) sum += bytes[i]; bytes[255] = unchecked((byte)-sum);
        }
        internal void SetUnknownType3(bool unknown)
        {
            Require(bytes.Length == 256, "only the independent TypeIII input has this descriptor layout");
            bytes[139] = unknown ? (byte)9 : (byte)8;
            bytes[142] = 0; var sum = 0;
            for (var index = 129; index < 142; index++) sum += bytes[index];
            bytes[142] = unchecked((byte)-sum);
            bytes[255] = 0; sum = 0;
            for (var index = 128; index < 255; index++) sum += bytes[index];
            bytes[255] = unchecked((byte)-sum);
        }
        internal void SetUnknownDmt(bool unknown)
        {
            Require(bytes.Length == 640, "only a recorded advanced source carries a DisplayID code list");
            if (bitmap) bytes[533] = unknown ? (byte)1 : (byte)0; // Unknown bitmap header makes the whole mode value unavailable.
            else if (originalVic)
            {
                if (legacy) bytes[525] = unknown ? (byte)129 : (byte)16; // Full byte129 is unassigned, not native-bit VIC1.
                else bytes[528] = unknown ? (byte)1 : (byte)0; // Full0x0110 must not alias original CTA-VIC16.
            }
            else if (legacy) bytes[520] = unknown ? (byte)0 : (byte)4; // Type IV code0 is unknown; no high byte exists.
            else bytes[521] = unknown ? (byte)1 : (byte)0; // VIII full ID0104 must never alias DMT04.
            var checksumOffset = checked(517 + bytes[514]);
            bytes[checksumOffset] = 0; var sum = 0;
            for (var index = 513; index < checksumOffset; index++) sum += bytes[index];
            bytes[checksumOffset] = unchecked((byte)-sum);
            bytes[639] = 0; sum = 0;
            for (var index = 512; index < 639; index++) sum += bytes[index];
            bytes[639] = unchecked((byte)-sum);
        }
        public DeviceTopologySnapshot ReadSnapshot()
        {
            Reads++;
            if (Fail) throw new InvalidOperationException("recorded EDID read failure");
            captured = new DeviceTopologyDisplayPath(1, 2, 3, 4, 5, 0, "Recorded Panel", @"\\?\DISPLAY#RECORDED#1",
                3840, 2160, 60000, 1001, false, false, @"PCI\RECORDED_GPU") {
                Edid = EdidParser.Parse(bytes), EdidRawDeclaredBase64 = Convert.ToBase64String(bytes),
                EdidObservationComplete = true };
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []);
        }
        internal DeviceTopologyCaptureOwner CreateOwner()
            => new(new NoAcpi(), new(this,
                _ => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                (_, time) => WindowsInformationFieldProjection.Displays(
                    captured is null ? [] : [WindowsDisplayPathTopologyReader.ToInformation(captured, Identity)], time),
                static (_, _) => Task.CompletedTask, static _ => new("recorded-edid-failure", []),
                static (_, _, _, _) => { }, static _ => { }, static () => { }));
        private static byte[] AdvancedBytes(bool legacy, bool originalVic, bool bitmap, byte containerRevision)
        {
            var bytes = new byte[640]; Bytes().CopyTo(bytes, 0); bytes[126] = 4;
            byte[] selection = [0xE6, 0x0D, 146, 129, 16, 146, 130, 0xE7, 0x08, 145, 0x81, 255, 255, 254, 255];
            byte[] timing = [0x13, 0x44, 2, 0x84, 0x7F, 7, 0x17, 1, 0x57, 0x80, 0x2B, 0, 0x37, 4, 0x2C, 0, 3, 0x80, 4, 0];
            var fractional = timing.ToArray(); fractional[0] = 0x7D; fractional[1] = 0x43;
            byte[] timings = [0xF6, 0x22, 2, .. timing, 0xF6, 0x22, 2, .. fractional];
            byte[] additional = [0xE6, 0x23, 0x29, 4, 0, 0x58, 0,
                0xEA, 0x2A, 0x20, 0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1, 0xE2, 0x0D, 147];
            for (var block = 1; block <= 3; block++)
            {
                var offset = block * 128; var payload = block switch { 1 => selection, 2 => timings, _ => additional };
                bytes[offset] = 2; bytes[offset + 1] = 3; bytes[offset + 2] = checked((byte)(4 + payload.Length));
                payload.CopyTo(bytes, offset + 4);
            }
            byte[] displayId = legacy
                ? [0x70, 0x12, 5, 2, 0, 6, 1, 2, 4, 0x58]
                : [0x70, 0x20, 7, 2, 0, 0x23, 8, 4, 4, 0, 0x58, 0];
            if (originalVic)
            {
                byte[] codes = legacy ? [6, 0x41, 2, 16, 193, 6, 0x81, 2, 1, 4] : [0x23, 0x48, 4, 16, 0, 193, 0, 0x23, 0x88, 4, 1, 0, 4, 0];
                displayId = [.. displayId, .. codes]; displayId[2] = checked((byte)(displayId[2] + codes.Length));
            }
            if (bitmap)
            {
                Require(legacy && originalVic && containerRevision is 0x12, "bitmap only extends either explicit legacy13-mode fixture");
                byte[] bitmaps = [0x07, 0, 1, 0x88, 0x08, 0, 1, 0x81];
                displayId = [.. displayId, .. bitmaps];
                displayId[1] = containerRevision; displayId[2] = checked((byte)(displayId[2] + bitmaps.Length));
            }
            displayId.CopyTo(bytes, 512); var displayIdSum = 0; var checksumOffset = checked(517 + bytes[514]);
            for (var index = 513; index < checksumOffset; index++) displayIdSum += bytes[index];
            bytes[checksumOffset] = unchecked((byte)-displayIdSum);
            for (var block = 0; block < 5; block++)
            {
                var offset = block * 128; bytes[offset + 127] = 0; var sum = 0;
                for (var index = 0; index < 127; index++) sum += bytes[offset + index];
                bytes[offset + 127] = unchecked((byte)-sum);
            }
            return bytes;
        }

        private static byte[] Bytes()
        {
            var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
            bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[24] = 2;
            for (var offset = 38; offset < 54; offset += 2) { bytes[offset] = 1; bytes[offset + 1] = 1; }
            new byte[] { 0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C, 0x45, 0, 0, 0, 0, 0, 0, 0x1E }.CopyTo(bytes, 54);
            bytes.AsSpan(54, 18).CopyTo(bytes.AsSpan(72)); bytes[72] = 0xF3; bytes[73] = 0x39;
            bytes[93] = 0x10; bytes[111] = 0x10;
            bytes[127] = unchecked((byte)-bytes.Sum(static value => (int)value)); return bytes;
        }
    }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("EDID does not query ACPI"); }
    private static void Require(bool value, string contract)
    { if (!value) throw new InvalidOperationException("EDID mode recorded consumer: " + contract); }
    private static void VerifyAdvanced(FieldReading reading, bool legacy = false, bool originalVic = false, bool bitmap = false, byte containerRevision = 0x12)
    {
        Verify(reading, bitmap ? 17 : originalVic ? 13 : 9); // The original two-DTD assertions still apply to the first two modes.
        var value = reading.Value!.Structure!.Value; var modes = value.GetProperty("modes");
        for (var index = 0; index < 2; index++)
        {
            var mode = modes[index + 2]; var timing = mode.GetProperty("timing"); var rate = timing.GetProperty("frameRate");
            Require(mode.GetProperty("encoding").GetString() == "cta-vtdb-vii"
                && mode.GetProperty("blockIndex").GetUInt32() == 2 && mode.GetProperty("byteOffset").GetUInt32() == (index == 0 ? 7u : 30u)
                && mode.GetProperty("revision").GetByte() == 2 && mode.GetProperty("containerFlags").GetByte() == 2
                && mode.GetProperty("containerRevision").GetByte() == 3
                && mode.GetProperty("dscPassThrough").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("preferred").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("native").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Encoding").GetBoolean()
                && mode.GetProperty("rawDescriptor").GetArrayLength() == 20
                && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
                && timing.GetProperty("pixelClockHz").GetUInt64() == (index == 0 ? 148_500_000UL : 148_350_000UL)
                && rate.GetProperty("numerator").GetUInt64() == (index == 0 ? 60UL : 1978UL)
                && rate.GetProperty("denominator").GetUInt64() == (index == 0 ? 1UL : 33UL),
                "CTA T7 source, exact rational and null DSC, independent of DisplayID container or live mode");
        }
        var selections = value.GetProperty("selectionDeclarations");
        Require(selections.GetArrayLength() == 3, "two original and one formula selection blocks retained");
        var vfp = selections[0]; var references = vfp.GetProperty("references");
        Require(vfp.GetProperty("encoding").GetString() == "cta-vfpdb" && vfp.GetProperty("blockIndex").GetUInt32() == 1
            && vfp.GetProperty("byteOffset").GetUInt32() == 6 && vfp.GetProperty("revision").GetByte() == 3
            && references.GetArrayLength() == 5, "original VFP container and complete ordered preference sequence");
        byte[] svrs = [146, 129, 16, 146, 130]; int[] targets = [3, 0, -1, 3, 1];
        uint[] blocks = [2, 0, 0, 2, 0]; uint[] offsets = [30, 54, 0, 30, 72];
        for (var index = 0; index < svrs.Length; index++)
        {
            var reference = references[index];
            Require(vfp.GetProperty("rawDescriptor")[index].GetByte() == svrs[index]
                && reference.GetProperty("shortVideoReference").GetByte() == svrs[index]
                && reference.GetProperty("byteOffset").GetUInt32() == (uint)(6 + index), "original SVR bytes/order/duplicates retained");
            if (targets[index] < 0)
            {
                Require(reference.GetProperty("targetEncoding").GetString() == "cta-vic"
                    && reference.GetProperty("code").GetUInt32() == 16
                    && reference.GetProperty("target").ValueKind == System.Text.Json.JsonValueKind.Null,
                    "direct VIC needs no VDB and does not synthesize a mode");
                continue;
            }
            var target = reference.GetProperty("target");
            Require(reference.GetProperty("targetEncoding").GetString() == (blocks[index] == 0 ? "edid-detailed" : "cta-vtdb-vii")
                && reference.GetProperty("code").ValueKind == System.Text.Json.JsonValueKind.Null
                && target.GetProperty("blockIndex").GetUInt32() == blocks[index]
                && target.GetProperty("byteOffset").GetUInt32() == offsets[index]
                && target.GetProperty("rawDescriptor").GetRawText() == modes[targets[index]].GetProperty("rawDescriptor").GetRawText(),
                "same-call global DTD/T7 target exact source and raw bytes");
        }
        var nvr = selections[1]; var native = nvr.GetProperty("references")[0]; var size = nvr.GetProperty("imageSize");
        Require(nvr.GetProperty("encoding").GetString() == "cta-nvrdb" && nvr.GetProperty("blockIndex").GetUInt32() == 1
            && nvr.GetProperty("byteOffset").GetUInt32() == 13 && nvr.GetProperty("flags").GetByte() == 0x81
            && nvr.GetProperty("rawDescriptor").GetArrayLength() == 6 && nvr.GetProperty("references").GetArrayLength() == 1
            && native.GetProperty("shortVideoReference").GetByte() == 145
            && native.GetProperty("targetEncoding").GetString() == "cta-vtdb-vii"
            && native.GetProperty("target").GetProperty("blockIndex").GetUInt32() == 2
            && native.GetProperty("target").GetProperty("byteOffset").GetUInt32() == 7
            && native.GetProperty("target").GetProperty("rawDescriptor").GetRawText() == modes[2].GetProperty("rawDescriptor").GetRawText()
            && size.GetProperty("widthCode").GetUInt32() == 65535 && size.GetProperty("heightCode").GetUInt32() == 65534
            && size.GetProperty("unitMicrometers").GetUInt32() == 1000, "NVR original code and declared unit, not observed panel dimensions");
        VerifyCodeTimings(value);
        VerifyFormulaTiming(value);
        VerifyDisplayIdCodes(value, legacy, containerRevision);
        if (originalVic) VerifyDisplayIdOriginalVicDomains(value, legacy, containerRevision);
        if (bitmap) VerifyDisplayIdBitmaps(value, containerRevision);
    }


    private static void VerifyDisplayIdBitmaps(System.Text.Json.JsonElement value, byte containerRevision)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 17, "all original13 plus four bitmap declarations");
        uint[] codes = [4, 8, 1, 8], widths = [640, 800, 640, 720], heights = [480, 600, 480, 240], rates = [60, 0, 60, 60];
        int[] bits = [3, 7, 0, 7];
        for (var i = 0; i < 4; i++)
        {
            var mode = modes[i + 13]; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == (i < 2 ? "displayid-dmt-bitmap" : "displayid-cta-bitmap")
                && mode.GetProperty("blockIndex").GetUInt32() == 4 && mode.GetProperty("byteOffset").GetUInt32() == (i < 2 ? 23u : 27u)
                && mode.GetProperty("bitIndex").GetInt32() == bits[i] && mode.GetProperty("code").GetUInt32() == codes[i]
                && mode.GetProperty("width").GetUInt32() == widths[i] && mode.GetProperty("height").GetUInt32() == heights[i]
                && !mode.GetProperty("interlaced").GetBoolean() && mode.GetProperty("revision").GetByte() == 0
                && mode.GetProperty("containerFlags").GetByte() == 0 && mode.GetProperty("containerRevision").GetByte() == containerRevision
                && raw.GetArrayLength() == 1 && raw[0].GetByte() == (i < 2 ? 0x88 : 0x81)
                && (i == 1 ? mode.GetProperty("nominalVerticalRateHz").ValueKind == System.Text.Json.JsonValueKind.Null
                    : mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[i])
                && (i < 2 ? !mode.GetProperty("reducedBlanking").GetBoolean()
                    : mode.GetProperty("reducedBlanking").ValueKind == System.Text.Json.JsonValueKind.Null),
                "bitmap code/BitIndex/full raw/source/container/verified nominal facts");
            foreach (var name in new[] { "timing", "native", "preferred", "dscPassThrough", "yCbCr420Encoding",
                "yCbCr420Supported", "yCbCr420Only", "yCbCr420CapabilityMap", "formula", "cvtParameters" })
                Require(mode.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null, "bitmap never borrows metadata: " + name);
        }
        foreach (var selection in value.GetProperty("selectionDeclarations").EnumerateArray())
        foreach (var reference in selection.GetProperty("references").EnumerateArray())
        {
            var target = reference.GetProperty("target");
            Require(!reference.GetProperty("targetEncoding").GetString()!.Contains("bitmap", StringComparison.Ordinal)
                && (target.ValueKind == System.Text.Json.JsonValueKind.Null || target.GetProperty("blockIndex").GetUInt32() != 4),
                "bitmap declarations remain outside original CTA reference domain");
        }
    }

    private static void VerifyCodeTimings(System.Text.Json.JsonElement value)
    {
        var modes = value.GetProperty("modes");
        for (var index = 0; index < 2; index++)
        {
            var mode = modes[index + 4]; var code = index == 0 ? 4u : 0x58u; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == "cta-dmt-code" && mode.GetProperty("code").GetUInt32() == code
                && mode.GetProperty("blockIndex").GetUInt32() == 3 && mode.GetProperty("byteOffset").GetUInt32() == (index == 0 ? 7u : 9u)
                && mode.GetProperty("revision").GetByte() == 1 && mode.GetProperty("containerFlags").GetByte() == 0x29
                && mode.GetProperty("containerRevision").GetByte() == 3
                && raw.GetArrayLength() == 2 && raw[0].GetByte() == code && raw[1].GetByte() == 0
                && mode.GetProperty("width").GetUInt32() == (index == 0 ? 640u : 4096u)
                && mode.GetProperty("height").GetUInt32() == (index == 0 ? 480u : 2160u)
                && mode.GetProperty("reducedBlanking").GetBoolean() == (index != 0)
                && (index == 0 ? mode.GetProperty("nominalVerticalRateHz").GetUInt32() == 60
                    : mode.GetProperty("nominalVerticalRateHz").ValueKind == System.Text.Json.JsonValueKind.Null)
                && mode.GetProperty("timing").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("dscPassThrough").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("cvtParameters").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Encoding").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Only").ValueKind == System.Text.Json.JsonValueKind.Null
                && mode.GetProperty("yCbCr420Supported").GetBoolean(),
                "T8 full LE16 DMT code/raw/source, known nominal only, also420 capability without encoding or generated timing");
        }
    }

    private static void VerifyDisplayIdOriginalVicDomains(System.Text.Json.JsonElement value, bool legacy, byte containerRevision = 0x12)
    {
        var modes = value.GetProperty("modes");
        uint[] codes = [16, 193, 1, 4]; uint[] widths = [1920, 5120, 3840, 4096]; uint[] rates = [60, 120, 30, 24];
        uint[] offsets = legacy ? [13, 14, 18, 19] : [15, 17, 22, 24];
        for (var i = 0; i < codes.Length; i++)
        {
            var mode = modes[i + 9]; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("code").GetUInt32() == codes[i]
                && mode.GetProperty("encoding").GetString() == (legacy ? "displayid-iv-" : "displayid-") + (i < 2 ? "cta-vic" : "hdmi-vic")
                && mode.GetProperty("blockIndex").GetUInt32() == 4 && mode.GetProperty("byteOffset").GetUInt32() == offsets[i]
                && mode.GetProperty("width").GetUInt32() == widths[i] && mode.GetProperty("height").GetUInt32() == (i == 0 ? 1080u : 2160u)
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[i] && !mode.GetProperty("interlaced").GetBoolean()
                && mode.GetProperty("revision").GetByte() == (legacy ? 1 : 0)
                && mode.GetProperty("containerRevision").GetByte() == (legacy ? containerRevision : 0x20)
                && mode.GetProperty("containerFlags").GetByte() == ((i < 2 ? 0x40 : 0x80) | (legacy ? 1 : 8))
                && raw.GetArrayLength() == (legacy ? 1 : 2) && raw[0].GetUInt32() == codes[i] && (legacy || raw[1].GetByte() == 0),
                "DisplayID original CTA-VIC/HDMI-VIC complete code, source and reliable nominal facts");
            foreach (var name in new[] { "timing", "native", "preferred", "reducedBlanking", "dscPassThrough", "yCbCr420Supported", "yCbCr420Only", "formula", "cvtParameters" })
                Require(mode.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null, "DisplayID code domain does not borrow wrapper metadata: " + name);
        }
    }

    private static void VerifyDisplayIdCodes(System.Text.Json.JsonElement value, bool legacy, byte containerRevision = 0x12)
    {
        var modes = value.GetProperty("modes");
        for (var index = 0; index < 2; index++)
        {
            var mode = modes[index + 7]; var code = index == 0 ? 4u : 0x58u; var raw = mode.GetProperty("rawDescriptor");
            Require(mode.GetProperty("encoding").GetString() == (legacy ? "displayid-iv-dmt-code" : "displayid-dmt-code") && mode.GetProperty("code").GetUInt32() == code
                && mode.GetProperty("blockIndex").GetUInt32() == 4 && mode.GetProperty("byteOffset").GetUInt32() == (index == 0 ? 8u : legacy ? 9u : 10u)
                && mode.GetProperty("revision").GetByte() == (legacy ? 1 : 0) && mode.GetProperty("containerFlags").GetByte() == (legacy ? 1 : 8)
                && mode.GetProperty("containerRevision").GetByte() == (legacy ? containerRevision : 0x20)
                && raw.GetArrayLength() == (legacy ? 1 : 2) && raw[0].GetByte() == code && (legacy || raw[1].GetByte() == 0)
                && mode.GetProperty("width").GetUInt32() == (index == 0 ? 640u : 4096u)
                && mode.GetProperty("height").GetUInt32() == (index == 0 ? 480u : 2160u)
                && !mode.GetProperty("interlaced").GetBoolean() && mode.GetProperty("reducedBlanking").GetBoolean() == (index != 0)
                && (index == 0 ? mode.GetProperty("nominalVerticalRateHz").GetUInt32() == 60
                    : mode.GetProperty("nominalVerticalRateHz").ValueKind == System.Text.Json.JsonValueKind.Null),
                legacy ? "DisplayID13 IV byte DMT/raw/source/header and reliable nominal facts" : "DisplayID20 VIII full DMT/raw/source/header and reliable nominal facts");
            foreach (var name in new[] { "timing", "dscPassThrough", "preferred", "native", "yCbCr420Only", "yCbCr420Encoding",
                "yCbCr420Supported", "yCbCr420CapabilityMap", "formula", "cvtParameters", "bitIndex" })
                Require(mode.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null,
                    "DisplayID IV/VIII does not borrow CTA metadata or generate timing: " + name);
        }
        foreach (var selection in value.GetProperty("selectionDeclarations").EnumerateArray())
        foreach (var reference in selection.GetProperty("references").EnumerateArray())
        {
            var target = reference.GetProperty("target");
            Require(reference.GetProperty("targetEncoding").GetString() is not ("displayid-dmt-code" or "displayid-iv-dmt-code")
                && (target.ValueKind == System.Text.Json.JsonValueKind.Null || target.GetProperty("blockIndex").GetUInt32() != 4),
                "DisplayID codes remain outside the unchanged CTA DTD/T7/T10 selection domain");
        }
    }

    private static void VerifyFormulaTiming(System.Text.Json.JsonElement value)
    {
        var mode = value.GetProperty("modes")[6]; var raw = mode.GetProperty("rawDescriptor");
        byte[] body = [0x9B, 0x7F, 7, 0x37, 4, 0x3B, 0x58, 1];
        Require(raw.GetArrayLength() == body.Length, "T10 complete eight-byte descriptor");
        for (var index = 0; index < body.Length; index++) Require(raw[index].GetByte() == body[index], "T10 original raw parameter bytes");
        Require(mode.GetProperty("encoding").GetString() == "cta-vtdb-x" && mode.GetProperty("blockIndex").GetUInt32() == 3
            && mode.GetProperty("byteOffset").GetUInt32() == 14 && mode.GetProperty("revision").GetByte() == 0
            && mode.GetProperty("containerFlags").GetByte() == 0x20 && mode.GetProperty("containerRevision").GetByte() == 3
            && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
            && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == 60 && mode.GetProperty("reducedBlanking").GetBoolean()
            && mode.GetProperty("formula").GetString() == "cvt" && !mode.GetProperty("interlaced").GetBoolean()
            && mode.GetProperty("yCbCr420Encoding").GetBoolean() && mode.GetProperty("yCbCr420Supported").GetBoolean()
            && mode.GetProperty("timing").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("dscPassThrough").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("code").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("preferred").ValueKind == System.Text.Json.JsonValueKind.Null
            && mode.GetProperty("native").ValueKind == System.Text.Json.JsonValueKind.Null,
            "T10 CTA source and declared geometry/formula, no generated clock or winning/native mode");
        var parameters = mode.GetProperty("cvtParameters");
        Require(parameters.GetProperty("reducedBlankingVersion").GetByte() == 3 && parameters.GetProperty("stereoCode").GetByte() == 0
            && parameters.GetProperty("alternativeRefresh1000Over1001Supported").ValueKind == System.Text.Json.JsonValueKind.Null
            && parameters.GetProperty("horizontalBlankingBase160Pixels").GetBoolean()
            && parameters.GetProperty("earlyVsyncRequired").GetBoolean()
            && parameters.GetProperty("deltaHorizontalBlankingCode").GetByte() == 6
            && parameters.GetProperty("additionalVerticalBlankingCode").GetByte() == 2
            && parameters.GetProperty("alternativeMinimumVerticalBlanking").GetBoolean(), "T10 original RB3 CVT parameter codes");
        var selection = value.GetProperty("selectionDeclarations")[2]; var references = selection.GetProperty("references");
        Require(selection.GetProperty("encoding").GetString() == "cta-vfpdb" && selection.GetProperty("blockIndex").GetUInt32() == 3
            && selection.GetProperty("byteOffset").GetUInt32() == 24 && selection.GetProperty("revision").GetByte() == 3
            && selection.GetProperty("rawDescriptor").GetArrayLength() == 1 && selection.GetProperty("rawDescriptor")[0].GetByte() == 147
            && references.GetArrayLength() == 1, "new VFP preserves original SVR147 and its source");
        var reference = references[0]; var target = reference.GetProperty("target");
        Require(reference.GetProperty("shortVideoReference").GetByte() == 147 && reference.GetProperty("byteOffset").GetUInt32() == 24
            && reference.GetProperty("targetEncoding").GetString() == "cta-vtdb-x"
            && reference.GetProperty("code").ValueKind == System.Text.Json.JsonValueKind.Null
            && target.GetProperty("blockIndex").GetUInt32() == 3 && target.GetProperty("byteOffset").GetUInt32() == 14
            && target.GetProperty("rawDescriptor").GetRawText() == raw.GetRawText(),
            "same-call third T7/T10 target is exact T10 source; intervening T8 modes do not consume the reference domain");
    }

    private static void Verify(FieldReading reading, int expectedModeCount = 2)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid"
            && reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.Value?.Structure is not null, "exact caller-owned display identity and structured declared value");
        var value = reading.Value!.Structure!.Value;
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == expectedModeCount && value.GetProperty("ranges").GetArrayLength() == 0,
            "complete two-DTD directory preserved, not collapsed by resolution");
        for (var i = 0; i < 2; i++)
        {
            var mode = modes[i]; var timing = mode.GetProperty("timing"); var rate = timing.GetProperty("frameRate");
            Require(mode.GetProperty("encoding").GetString() == "edid-detailed"
                && mode.GetProperty("blockIndex").GetUInt32() == 0
                && mode.GetProperty("byteOffset").GetUInt32() == (uint)(54 + 18 * i)
                && mode.GetProperty("width").GetUInt32() == 1920 && mode.GetProperty("height").GetUInt32() == 1080
                && mode.GetProperty("rawDescriptor").GetArrayLength() == 18
                && timing.GetProperty("pixelClockHz").GetUInt64() == (i == 0 ? 148_500_000UL : 148_350_000UL)
                && rate.GetProperty("numerator").GetUInt64() == (i == 0 ? 60UL : 1978UL)
                && rate.GetProperty("denominator").GetUInt64() == (i == 0 ? 1UL : 33UL),
                "source position/bytes/exact rational timing independent of active stream");
        }
    }
}

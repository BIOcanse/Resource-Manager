using System.Diagnostics.Eventing.Reader;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    // Aggregate bounded bins, never retain individual GPU deliveries. Receipt time
    // and ETW event time are separate: delivery after CaptureState returns does
    // not imply that the provider emitted that event after rundown completed.
    private sealed class ActualUseGpuMeasurement(Guid provider, int[] allowedIds) : GpuAllocationObservation.ISink
    {
        private readonly object gate = new();
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private readonly HashSet<int> allowed = allowedIds.ToHashSet();
        private readonly Dictionary<BinKey, Bin> bins = [];
        private readonly List<GpuAllocationObservation.Command> commands = [];
        private readonly HashSet<int> rundownIds = [];
        private readonly List<object> rundownMetadata = [];
        private string? metadataError;
        private long overflow;
        private DateTimeOffset? enabled;
        private DateTimeOffset? enableReturned;
        private DateTimeOffset? capture;
        private DateTimeOffset? captureReturned;
        private DateTimeOffset? filterReenable;
        private DateTimeOffset? filterReenableReturned;
        private DateTimeOffset? observationEnded;
        private const int MaximumBins = 100_000;
        private const int MaximumCommands = 128;
        internal static readonly TimeSpan RestorationComparisonGrace = TimeSpan.FromSeconds(1);
        private readonly record struct BinKey(Guid Provider, int Id, int ReceiptSecond, int EventSecond,
            string ReceiptPhase, string EventPhase, bool Accepted, bool AfterObservation, bool AfterRestorationGrace);
        private sealed class Bin
        {
            internal long Count;
            internal DateTimeOffset FirstReceipt;
            internal DateTimeOffset LastReceipt;
            internal DateTime FirstEventUtc;
            internal DateTime LastEventUtc;
        }

        internal void ReadRundownMetadata()
        {
            try
            {
                using var metadata = new ProviderMetadata("Microsoft-Windows-DxgKrnl");
                var remaining = 65_536;
                foreach (var item in metadata.Events)
                {
                    if (--remaining < 0) throw new InvalidDataException("Provider event metadata exceeded the bounded manifest limit.");
                    var opcode = item.Opcode;
                    if (opcode is null) continue;
                    // Windows manifest DC_Start/DC_Stop opcodes define known
                    // rundown IDs. Do not guess from a burst or an ID's name.
                    if (opcode.Name is not ("win:DC_Start" or "win:DC_Stop")
                        && opcode.Value is not (3 or 4 or 196608 or 262144)) continue;
                    var id = checked((int)item.Id);
                    rundownIds.Add(id);
                    rundownMetadata.Add(new { id, opcode = opcode.Name, opcodeValue = opcode.Value });
                }
            }
            catch (Exception error) { metadataError = error.ToString(); }
        }
        public void OnCommand(GpuAllocationObservation.Command value)
        {
            lock (gate)
            {
                if (commands.Count >= MaximumCommands) { overflow++; return; }
                commands.Add(value);
                if (value.Provider != provider) return;
                if (value.Operation == "enable")
                {
                    if (enabled is null && !value.Completed) enabled = value.At;
                    else if (enableReturned is null && value.Completed && value.Succeeded == true) enableReturned = value.At;
                    else if (captureReturned is not null)
                    {
                        if (!value.Completed) filterReenable = value.At;
                        else if (value.Succeeded == true) filterReenableReturned = value.At;
                    }
                }
                if (value.Operation == "capture-state")
                {
                    if (!value.Completed) capture = value.At;
                    else if (value.Succeeded == true) captureReturned = value.At;
                }
            }
        }
        public void OnDelivery(GpuAllocationObservation.Delivery value)
        {
            lock (gate)
            {
                var origin = enabled ?? started;
                var eventAt = new DateTimeOffset(DateTime.SpecifyKind(value.EventTimestampUtc, DateTimeKind.Utc));
                var cutoff = filterReenableReturned + RestorationComparisonGrace;
                var key = new BinKey(value.Provider, value.Id, Second(value.ReceivedAt - origin), Second(eventAt - origin),
                    Phase(value.ReceivedAt), Phase(eventAt), value.Accepted, observationEnded is not null,
                    cutoff is not null && value.ReceivedAt >= cutoff && eventAt >= cutoff);
                if (!bins.TryGetValue(key, out var bin))
                {
                    if (bins.Count >= MaximumBins) { overflow++; return; }
                    bin = new() { FirstReceipt = value.ReceivedAt, FirstEventUtc = value.EventTimestampUtc };
                    bins.Add(key, bin);
                }
                bin.Count++;
                bin.LastReceipt = value.ReceivedAt;
                bin.LastEventUtc = value.EventTimestampUtc;
            }
        }
        internal void FinishObservation()
        {
            lock (gate) observationEnded ??= DateTimeOffset.UtcNow;
        }
        private static int Second(TimeSpan value) => (int)Math.Clamp(Math.Floor(value.TotalSeconds), -120d, 120d);
        private string Phase(DateTimeOffset at)
        {
            if (enabled is null) return "enable-not-recorded";
            if (at < enabled) return "before-enable";
            if (enableReturned is null || at < enableReturned) return "inside-enable-call";
            if (capture is null || at < capture) return "after-enable-before-capture";
            if (captureReturned is null || at < captureReturned) return "inside-capture-state-call";
            if (filterReenable is not null && at >= filterReenable)
            {
                if (filterReenableReturned is null || at < filterReenableReturned) return "inside-filter-reenable-call";
                return "after-filter-reenable-return";
            }
            return "after-capture-state-return";
        }
        internal int[] ReadCaptureStateNeededRundownIds()
        {
            lock (gate)
                return bins.Where(pair => pair.Key.Provider == provider && !pair.Key.AfterObservation
                    && pair.Key.EventPhase == "inside-capture-state-call" && allowed.Contains(pair.Key.Id) && rundownIds.Contains(pair.Key.Id))
                    .Select(pair => pair.Key.Id).Distinct().Order().ToArray();
        }
        internal object ReadRestorationEvidence(TimeSpan comparisonGrace, int[]? sameRunControlNeededIds = null)
        {
            lock (gate)
            {
                if (comparisonGrace != RestorationComparisonGrace) throw new ArgumentOutOfRangeException(nameof(comparisonGrace));
                var cutoff = filterReenableReturned + comparisonGrace;
                var active = bins.Where(pair => pair.Key.Provider == provider && !pair.Key.AfterObservation).ToArray();
                var later = active.Where(pair => pair.Key.AfterRestorationGrace).ToArray();
                var unread = later.Where(pair => !allowed.Contains(pair.Key.Id)).ToArray();
                var seenDuringCapture = active.Where(pair => pair.Key.EventPhase == "inside-capture-state-call")
                    .Select(pair => pair.Key.Id).Distinct().Order().ToArray();
                var observedNeeded = seenDuringCapture.Where(id => allowed.Contains(id) && rundownIds.Contains(id)).ToArray();
                var missing = sameRunControlNeededIds?.Except(observedNeeded).Order().ToArray();
                return new { initialEnable = enabled, capture, captureReturned, filterReenable, filterReenableReturned,
                    comparisonGrace, comparisonStartsAt = cutoff, observationEndsAt = observationEnded,
                    steadyDeliveredDxg = later.Sum(pair => pair.Value.Count), steadyUnreadDxg = unread.Sum(pair => pair.Value.Count),
                    steadyUnreadIds = unread.Select(pair => pair.Key.Id).Distinct().Order().ToArray(),
                    neededKnownRundownIds = allowed.Intersect(rundownIds).Order().ToArray(),
                    deliveredIdsEmittedDuringCaptureState = seenDuringCapture,
                    neededKnownRundownIdsEmittedDuringCaptureState = observedNeeded,
                    sameRunControlNeededRundownIds = sameRunControlNeededIds,
                    sameRunMissingNeededRundownIds = missing,
                    sameRunComparison = sameRunControlNeededIds is null ? "not-requested-or-control-not-qualified"
                        : sameRunControlNeededIds.Length == 0 ? "inconclusive-empty-control-set"
                        : missing!.Length == 0 ? "all-control-ids-observed" : "missing-control-ids-causality-unproven",
                    proofLimit = "The comparison excludes the explicit one-second grace and any event emitted before its cutoff, even if buffered delivery arrived later. The same-run raw CaptureState control supplies required observed IDs; globally declared but unobserved IDs are not required. Missing IDs fail acceptance, but their cause remains unproven because workload may have changed. Facts are never filled in or rebuilt. A short private filter probe does not independently prove per-object allocation-ledger completeness." };
            }
        }
        internal void RequireRestoredFilter(TimeSpan comparisonGrace)
        {
            lock (gate)
            {
                if (comparisonGrace != RestorationComparisonGrace) throw new ArgumentOutOfRangeException(nameof(comparisonGrace));
                if (filterReenableReturned is null)
                    throw new InvalidDataException("No successful filtered EnableProvider after CaptureState was recorded.");
                var cutoff = filterReenableReturned.Value + comparisonGrace;
                if (observationEnded is null || observationEnded.Value <= cutoff)
                    throw new InvalidDataException("No post-restoration comparison interval remained after the bounded grace.");
                if (metadataError is not null)
                    throw new InvalidDataException("Rundown ID metadata was not completely available: " + metadataError);
                if (!bins.Any(pair => pair.Key.Provider == provider && !pair.Key.AfterObservation
                    && pair.Key.EventPhase == "inside-capture-state-call" && allowed.Contains(pair.Key.Id) && rundownIds.Contains(pair.Key.Id)))
                    throw new InvalidDataException("No consumed known rundown ID was emitted during CaptureState; filter restoration cannot pass without positive rundown evidence.");
                var later = bins.Where(pair => pair.Key.Provider == provider && !pair.Key.AfterObservation
                    && pair.Key.AfterRestorationGrace).ToArray();
                if (later.Sum(pair => pair.Value.Count) == 0)
                    throw new InvalidDataException("No newly emitted DxgKrnl delivery in the post-restoration interval; an empty interval cannot prove the filter.");
                var unread = later.Where(pair => !allowed.Contains(pair.Key.Id)).Select(pair => pair.Key.Id).Distinct().Order().ToArray();
                if (unread.Length != 0)
                    throw new InvalidDataException($"Unread newly emitted DxgKrnl IDs remain after filtered re-enable and grace: [{string.Join(",", unread)}].");
            }
        }
        internal object Read()
        {
            lock (gate)
            {
                var rows = bins.Select(pair => new {
                    provider = pair.Key.Provider, id = pair.Key.Id, allowed = pair.Key.Provider == provider && allowed.Contains(pair.Key.Id),
                    knownRundown = pair.Key.Provider == provider && rundownIds.Contains(pair.Key.Id),
                    receiptSecondFromEnable = pair.Key.ReceiptSecond, eventSecondFromEnable = pair.Key.EventSecond,
                    pair.Key.ReceiptPhase, pair.Key.EventPhase, pair.Key.Accepted, pair.Key.AfterObservation, pair.Key.AfterRestorationGrace, pair.Value.Count,
                    pair.Value.FirstReceipt, pair.Value.LastReceipt, pair.Value.FirstEventUtc, pair.Value.LastEventUtc
                }).OrderBy(value => value.receiptSecondFromEnable).ThenBy(value => value.id).ToArray();
                var dxg = bins.Where(pair => pair.Key.Provider == provider && !pair.Key.AfterObservation).ToArray();
                var tail = bins.Where(pair => pair.Key.Provider == provider && pair.Key.AfterObservation).ToArray();
                var noRead = dxg.Where(pair => !allowed.Contains(pair.Key.Id)).ToArray();
                // Post-enable second>=2 is a reproducible comparison interval,
                // not a claim that all rundown has finished after two seconds.
                var laterUnread = noRead.Where(pair => pair.Key.ReceiptSecond >= 2).Sum(pair => pair.Value.Count);
                var ended = observationEnded ?? DateTimeOffset.UtcNow;
                var measuredSeconds = (ended - (enabled ?? started)).TotalSeconds;
                var delivered = dxg.Sum(pair => pair.Value.Count);
                var accepted = dxg.Where(pair => pair.Key.Accepted).Sum(pair => pair.Value.Count);
                object[] Seconds(bool eventTime) => dxg.GroupBy(pair => eventTime ? pair.Key.EventSecond : pair.Key.ReceiptSecond)
                    .OrderBy(group => group.Key).Select(group => (object)new {
                        secondFromEnable = group.Key, delivered = group.Sum(pair => pair.Value.Count),
                        accepted = group.Where(pair => pair.Key.Accepted).Sum(pair => pair.Value.Count),
                        unread = group.Where(pair => !allowed.Contains(pair.Key.Id)).Sum(pair => pair.Value.Count),
                        knownRundown = group.Where(pair => rundownIds.Contains(pair.Key.Id)).Sum(pair => pair.Value.Count),
                        ids = group.GroupBy(pair => pair.Key.Id).OrderBy(kind => kind.Key).Select(kind => new {
                            id = kind.Key, allowed = allowed.Contains(kind.Key), knownRundown = rundownIds.Contains(kind.Key),
                            delivered = kind.Sum(pair => pair.Value.Count),
                            accepted = kind.Where(pair => pair.Key.Accepted).Sum(pair => pair.Value.Count),
                            rejected = kind.Where(pair => !pair.Key.Accepted).Sum(pair => pair.Value.Count)
                        }).ToArray()
                    }).ToArray();
                return new { started, ended, measuredSeconds, commands = commands.ToArray(),
                    allowedIds = allowedIds, knownRundownIds = rundownIds.Order().ToArray(), rundownMetadata = rundownMetadata.ToArray(), metadataError,
                    deliveredDxg = delivered, acceptedDxg = accepted,
                    deliveredPerSecond = measuredSeconds > 0 ? delivered / measuredSeconds : 0,
                    acceptedPerSecond = measuredSeconds > 0 ? accepted / measuredSeconds : 0,
                    rejectedPerSecond = measuredSeconds > 0 ? (delivered - accepted) / measuredSeconds : 0,
                    rejectedDxg = dxg.Where(pair => !pair.Key.Accepted).Sum(pair => pair.Value.Count),
                    postObservationDeliveredDxg = tail.Sum(pair => pair.Value.Count),
                    postObservationRejectedDxg = tail.Where(pair => !pair.Key.Accepted).Sum(pair => pair.Value.Count),
                    unreadDxg = noRead.Sum(pair => pair.Value.Count), unreadAfterSecond2 = laterUnread,
                    classification = noRead.Length == 0 ? "no-unread-ids-observed" : laterUnread == 0
                        ? "unread-observed-only-before-second-2-causality-unproven" : "unread-observed-at-or-after-second-2",
                    overflow, perReceiptSecond = Seconds(false), perEtwEventSecond = Seconds(true), rows,
                    proofLimit = "Time-relative bins distinguish receipt and ETW timestamps. DC_Start/DC_Stop metadata identifies known rundown IDs; time adjacency never proves CaptureState caused an event. Later delivery may still contain buffered rundown. Filter-only variants are required for capability attribution." };
            }
        }
        internal long Overflow { get { lock (gate) return overflow; } }
    }
}

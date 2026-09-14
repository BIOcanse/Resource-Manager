using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal enum ResourceBreakdownFailureTransition
{
    Entered,
    Changed,
    Repeated
}

internal sealed class ResourceBreakdownSnapshotState
{
    private readonly object gate;
    private Dictionary<string, DatasetEntry> datasets =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? lastAnyAttemptAt;
    private ulong scheduleWorkspaceIncarnation;
    private ulong scheduleConfigurationGeneration;
    private ulong scheduleStateRevision;
    private ulong schedulePlanEpoch;
    private DateTimeOffset? scheduleCommandAt;
    private NativeItemSamplingSubscriptionScheduleOrigin scheduleOrigin;
    private long stateRevision;

    public ResourceBreakdownSnapshotState(object? publicationGate = null)
    {
        gate = publicationGate ?? new object();
    }

    public ResourceBreakdownSnapshot? Read(DateTimeOffset now)
    {
        lock (gate)
        {
            return datasets.Count == 0
                && lastAnyAttemptAt is null
                    ? null
                    : CreateSnapshot(now, request: null);
        }
    }

    public ResourceBreakdownSnapshot ReadOrCreate(DateTimeOffset now)
    {
        lock (gate)
        {
            return CreateSnapshot(now, request: null);
        }
    }

    public ResourceBreakdownSnapshot ReadOrCreate(
        DateTimeOffset now,
        ResourceBreakdownSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            return CreateSnapshot(now, request);
        }
    }

    public bool AcceptSchedule(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        ValidateSchedule(schedule);
        lock (gate)
        {
            if (!IsNewerSchedule(schedule))
            {
                return false;
            }

            ApplySchedule(schedule);
            stateRevision += 1;
            return true;
        }
    }

    public void MarkAttempt(DateTimeOffset attemptedAt)
    {
        lock (gate)
        {
            lastAnyAttemptAt = attemptedAt;
            stateRevision += 1;
        }
    }

    public void MarkAttempt(
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var datasetIds = RequestedDatasetIds(request);
        lock (gate)
        {
            lastAnyAttemptAt = attemptedAt;
            foreach (var datasetId in datasetIds)
            {
                var dataset = GetOrCreateDataset(datasetId);
                dataset.LastAttemptAt = attemptedAt;
                dataset.StateRevision += 1;
            }
            stateRevision += 1;
        }
    }

    public ResourceBreakdownFailureTransition MarkFailed(
        DateTimeOffset attemptedAt,
        Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            lastAnyAttemptAt = attemptedAt;
            var transition = ResourceBreakdownFailureTransition.Repeated;
            foreach (var dataset in datasets.Values)
            {
                transition = MergeTransition(
                    transition,
                    SetDatasetFailure(
                        dataset,
                        attemptedAt,
                        error,
                        "resource-breakdown-dataset-sample-failed",
                        "资源数据集未能完成本轮采样。"));
            }
            stateRevision += 1;
            return transition;
        }
    }

    public ResourceBreakdownFailureTransition MarkFailed(
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt,
        Exception error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(error);
        var datasetIds = RequestedDatasetIds(request);
        lock (gate)
        {
            lastAnyAttemptAt = attemptedAt;
            var transition = ResourceBreakdownFailureTransition.Repeated;
            foreach (var datasetId in datasetIds)
            {
                transition = MergeTransition(
                    transition,
                    SetDatasetFailure(
                        GetOrCreateDataset(datasetId),
                        attemptedAt,
                        error,
                        "resource-breakdown-dataset-sample-failed",
                        "资源数据集未能完成本轮采样。"));
            }
            stateRevision += 1;
            return transition;
        }
    }

    public bool Covers(ResourceBreakdownSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metricIds = NormalizeRequestedMetricIds(request);
        lock (gate)
        {
            foreach (var metricId in metricIds)
            {
                var datasetId = SamplingDatasetIds.ForProcessMetric(metricId);
                if (!datasets.TryGetValue(datasetId, out var dataset)
                    || dataset.LastSuccessAt is null
                    || !dataset.Coverage.TryGetValue(metricId, out var coverage)
                    || coverage.ProcessDetailLevel < request.ProcessDetailLevel)
                {
                    return false;
                }
            }
            return metricIds.Length != 0;
        }
    }

    public bool ApplyScheduled(
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot sample,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sample);
        ValidateSchedule(schedule);
        lock (gate)
        {
            ApplySample(request, sample, attemptedAt);
            if (IsNewerSchedule(schedule))
            {
                ApplySchedule(schedule);
            }
            stateRevision += 1;
            return true;
        }
    }

    internal ResourceBreakdownSnapshotState PrepareScheduledCopy(
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot sample,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        lock (gate)
        {
            var prepared = CloneUnsafe();
            if (!prepared.ApplyScheduled(request, sample, attemptedAt, schedule))
            {
                throw new InvalidOperationException(
                    "Resource breakdown publication was prepared against a stale schedule.");
            }
            return prepared;
        }
    }

    internal ResourceBreakdownSnapshotState PrepareFailureCopy(
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt,
        Exception error,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        lock (gate)
        {
            var prepared = CloneUnsafe();
            _ = prepared.MarkFailed(request, attemptedAt, error);
            _ = prepared.AcceptSchedule(schedule);
            return prepared;
        }
    }

    internal bool RequestedMetricDatasetsCommitted(
        ResourceBreakdownSampleRequest request,
        DateTimeOffset capturedAt)
    {
        lock (gate)
        {
            var requested = NormalizeRequestedMetricIds(request);
            foreach (var datasetId in requested
                         .Select(SamplingDatasetIds.ForProcessMetric)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!datasets.TryGetValue(datasetId, out var dataset)
                    || dataset.FailureCode is not null
                    || dataset.LastSuccessAt != capturedAt
                    || dataset.CapturedAt != capturedAt)
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal void CommitPrepared(ResourceBreakdownSnapshotState prepared)
    {
        lock (gate)
        {
            datasets = prepared.datasets;
            lastAnyAttemptAt = prepared.lastAnyAttemptAt;
            scheduleWorkspaceIncarnation = prepared.scheduleWorkspaceIncarnation;
            scheduleConfigurationGeneration = prepared.scheduleConfigurationGeneration;
            scheduleStateRevision = prepared.scheduleStateRevision;
            schedulePlanEpoch = prepared.schedulePlanEpoch;
            scheduleCommandAt = prepared.scheduleCommandAt;
            scheduleOrigin = prepared.scheduleOrigin;
            stateRevision = prepared.stateRevision;
        }
    }

    private ResourceBreakdownSnapshotState CloneUnsafe()
    {
        var clone = new ResourceBreakdownSnapshotState(gate)
        {
            datasets = datasets.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
            lastAnyAttemptAt = lastAnyAttemptAt,
            scheduleWorkspaceIncarnation = scheduleWorkspaceIncarnation,
            scheduleConfigurationGeneration = scheduleConfigurationGeneration,
            scheduleStateRevision = scheduleStateRevision,
            schedulePlanEpoch = schedulePlanEpoch,
            scheduleCommandAt = scheduleCommandAt,
            scheduleOrigin = scheduleOrigin,
            stateRevision = stateRevision
        };
        return clone;
    }

    public void ApplyDirect(
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot sample,
        DateTimeOffset attemptedAt,
        TimeSpan defaultInterval,
        TimeSpan freshnessGrace)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sample);
        ValidatePositive(defaultInterval, nameof(defaultInterval));
        ValidatePositive(freshnessGrace, nameof(freshnessGrace));
        lock (gate)
        {
            ApplySample(request, sample, attemptedAt);
            stateRevision += 1;
        }
    }

    private void ApplySample(
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot sample,
        DateTimeOffset attemptedAt)
    {
        lastAnyAttemptAt = attemptedAt;
        var requestedMetricIds = NormalizeRequestedMetricIds(request);
        var requestedByDataset = requestedMetricIds.GroupBy(
            SamplingDatasetIds.ForProcessMetric,
            StringComparer.OrdinalIgnoreCase);
        var sampledByMetric = sample.Bars.ToDictionary(
            static bar => bar.MetricId,
            StringComparer.OrdinalIgnoreCase);

        foreach (var requestedDataset in requestedByDataset)
        {
            var dataset = GetOrCreateDataset(requestedDataset.Key);
            dataset.LastAttemptAt = attemptedAt;
            var requested = requestedDataset.ToArray();
            var replacement = requested
                .Where(sampledByMetric.ContainsKey)
                .Select(metricId => sampledByMetric[metricId])
                .ToArray();
            if (replacement.Length != requested.Length)
            {
                SetDatasetFailure(
                    dataset,
                    attemptedAt,
                    new InvalidOperationException(
                        $"Dataset '{requestedDataset.Key}' omitted one or more requested outputs."),
                    "resource-breakdown-dataset-output-missing",
                    "资源数据集本轮没有生成完整新值，当前数据集已置空。");
                continue;
            }

            if (replacement.Any(static bar =>
                    bar.ObservationStatus != SamplingObservationStatus.Current
                    || bar.AttributionStatus != SamplingObservationStatus.Current))
            {
                SetDatasetFailure(
                    dataset,
                    attemptedAt,
                    new InvalidOperationException(
                        $"Dataset '{requestedDataset.Key}' produced an unavailable or incomplete observation."),
                    "resource-breakdown-dataset-observation-not-current",
                    "资源数据集本轮观测不完整，当前数据集已置空。");
                continue;
            }

            dataset.Bars.Clear();
            dataset.Coverage.Clear();
            foreach (var bar in replacement)
            {
                dataset.Bars.Add(
                    bar.MetricId,
                    bar with
                    {
                        ScaleMode = ResourceBreakdownScaleModes.Normalize(
                            bar.MetricId,
                            scaleMode: null)
                    });
                dataset.Coverage.Add(
                    bar.MetricId,
                    new MetricCoverage(request.ProcessDetailLevel));
            }
            dataset.Generation = checked(dataset.Generation + 1);
            dataset.CapturedAt = sample.CapturedAt;
            dataset.LastSuccessAt = sample.CapturedAt;
            dataset.ReadyUntil = null;
            dataset.FailureCode = null;
            dataset.FailureMessage = null;
            dataset.FailureSignature = null;
            dataset.StateRevision += 1;
        }
    }

    private ResourceBreakdownSnapshot CreateSnapshot(
        DateTimeOffset now,
        ResourceBreakdownSampleRequest? request)
    {
        var requestedMetricIds = request is null
            ? null
            : NormalizeRequestedMetricIds(request);
        var requestedDatasetIds = requestedMetricIds is null
            ? datasets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : requestedMetricIds
                .Select(SamplingDatasetIds.ForProcessMetric)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var requestedMetricSet = requestedMetricIds?.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var selectedDatasets = requestedDatasetIds
            .Select(datasetId => datasets.GetValueOrDefault(datasetId)
                ?? new DatasetEntry(datasetId))
            .ToArray();
        var datasetStates = selectedDatasets
            .Select(CreateDatasetState)
            .ToArray();
        var bars = selectedDatasets
            .SelectMany(static dataset => dataset.Bars.Values)
            .Where(bar => requestedMetricSet is null
                || requestedMetricSet.Contains(bar.MetricId))
            .Select(bar => request is null
                ? bar
                : bar with
                {
                    ScaleMode = ResourceBreakdownScaleModes.Normalize(
                        bar.MetricId,
                        request.ScaleModes.GetValueOrDefault(bar.MetricId))
                })
            .OrderBy(
                static bar => bar.MetricId,
                MonitoringMetricIdPriorityComparer.Instance)
            .ToArray();
        var sampling = CreateAggregateSamplingState(datasetStates);
        var capturedAt = datasetStates
            .Where(static state => state.CapturedAt is not null)
            .Select(static state => state.CapturedAt!.Value)
            .DefaultIfEmpty(lastAnyAttemptAt ?? now)
            .Max();
        return new ResourceBreakdownSnapshot(capturedAt, bars)
        {
            Sampling = sampling,
            Datasets = datasetStates
        };
    }

    private static ResourceBreakdownDatasetSamplingState CreateDatasetState(
        DatasetEntry dataset)
    {
        var hasPayload = dataset.LastSuccessAt is not null
            && dataset.FailureCode is null;
        var status = hasPayload
            ? ResourceBreakdownSamplingStatuses.Ready
            : dataset.FailureCode is null
                ? ResourceBreakdownSamplingStatuses.Warming
                : ResourceBreakdownSamplingStatuses.Failed;
        return new ResourceBreakdownDatasetSamplingState(
            dataset.DatasetId,
            status,
            dataset.Generation,
            dataset.StateRevision,
            dataset.CapturedAt,
            dataset.LastAttemptAt,
            dataset.LastSuccessAt,
            dataset.ReadyUntil,
            dataset.FailureCode,
            dataset.FailureMessage);
    }

    private ResourceBreakdownSamplingState CreateAggregateSamplingState(
        IReadOnlyList<ResourceBreakdownDatasetSamplingState> selected)
        => ResourceBreakdownSamplingState.Aggregate(
            selected,
            stateRevision,
            lastAnyAttemptAt);

    private DatasetEntry GetOrCreateDataset(string datasetId)
    {
        if (!datasets.TryGetValue(datasetId, out var dataset))
        {
            dataset = new DatasetEntry(datasetId);
            datasets.Add(datasetId, dataset);
        }
        return dataset;
    }

    private static ResourceBreakdownFailureTransition SetDatasetFailure(
        DatasetEntry dataset,
        DateTimeOffset attemptedAt,
        Exception error,
        string code,
        string message)
    {
        var signature = CreateFailureSignature(error);
        var transition = dataset.FailureCode is null
            ? ResourceBreakdownFailureTransition.Entered
            : dataset.FailureCode == code
                && dataset.FailureSignature == signature
                ? ResourceBreakdownFailureTransition.Repeated
                : ResourceBreakdownFailureTransition.Changed;
        dataset.LastAttemptAt = attemptedAt;
        dataset.Bars.Clear();
        dataset.Coverage.Clear();
        dataset.CapturedAt = null;
        dataset.LastSuccessAt = null;
        dataset.ReadyUntil = null;
        dataset.FailureCode = code;
        dataset.FailureMessage = message;
        dataset.FailureSignature = signature;
        dataset.Generation = checked(dataset.Generation + 1);
        dataset.StateRevision += 1;
        return transition;
    }

    private static ResourceBreakdownFailureTransition MergeTransition(
        ResourceBreakdownFailureTransition current,
        ResourceBreakdownFailureTransition next)
        => (ResourceBreakdownFailureTransition)Math.Min(
            (int)current,
            (int)next);

    private static string CreateFailureSignature(Exception error)
    {
        var parts = new List<string>();
        for (Exception? current = error;
             current is not null;
             current = current.InnerException)
        {
            parts.Add($"{current.GetType().FullName}:{current.HResult}");
        }
        return string.Join(" -> ", parts);
    }

    private bool IsNewerSchedule(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        if (scheduleCommandAt is null)
        {
            return true;
        }

        var incarnation = schedule.WorkspaceIncarnation.CompareTo(
            scheduleWorkspaceIncarnation);
        if (incarnation != 0)
        {
            return incarnation > 0;
        }
        var generation = schedule.ConfigurationGeneration.CompareTo(
            scheduleConfigurationGeneration);
        if (generation != 0)
        {
            return generation > 0;
        }
        var revision = schedule.StateRevision.CompareTo(scheduleStateRevision);
        if (revision != 0)
        {
            return revision > 0;
        }
        var plan = schedule.PlanEpoch.CompareTo(schedulePlanEpoch);
        if (plan != 0)
        {
            return plan > 0;
        }
        var command = schedule.CommandAt.CompareTo(scheduleCommandAt.Value);
        if (command != 0)
        {
            return command > 0;
        }
        return (int)schedule.Origin > (int)scheduleOrigin;
    }

    private void ApplySchedule(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        scheduleWorkspaceIncarnation = schedule.WorkspaceIncarnation;
        scheduleConfigurationGeneration = schedule.ConfigurationGeneration;
        scheduleStateRevision = schedule.StateRevision;
        schedulePlanEpoch = schedule.PlanEpoch;
        scheduleCommandAt = schedule.CommandAt;
        scheduleOrigin = schedule.Origin;
    }

    private static void ValidateSchedule(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.WorkspaceIncarnation == 0
            || schedule.ConfigurationGeneration == 0
            || schedule.PlanEpoch == 0
            || schedule.DefaultInterval <= TimeSpan.Zero
            || schedule.FreshnessGrace <= TimeSpan.Zero
            || schedule.ActiveSourceCount < 0
            || schedule.ActiveItemCount < 0
            || schedule.DueItemCount < 0
            || schedule.ExpiredSourceCount < 0
            || schedule.IsActive && schedule.NextWakeAt is null)
        {
            throw new InvalidOperationException(
                "Resource breakdown sampling schedule is invalid.");
        }
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string[] NormalizeRequestedMetricIds(
        ResourceBreakdownSampleRequest request)
        => request.MetricIds
            .SelectMany(SplitMetricId)
            .Select(static metricId => metricId.Trim())
            .Where(static metricId => metricId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(MonitoringMetricIdPriorityComparer.Instance)
            .ToArray();

    private static string[] RequestedDatasetIds(
        ResourceBreakdownSampleRequest request)
        => NormalizeRequestedMetricIds(request)
            .Select(SamplingDatasetIds.ForProcessMetric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> SplitMetricId(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

    private sealed class DatasetEntry(string datasetId)
    {
        public string DatasetId { get; } = datasetId;
        public Dictionary<string, ResourceBreakdownBar> Bars { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MetricCoverage> Coverage { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public ulong Generation { get; set; }
        public long StateRevision { get; set; }
        public DateTimeOffset? CapturedAt { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? ReadyUntil { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
        public string? FailureSignature { get; set; }

        public DatasetEntry Clone()
        {
            var clone = new DatasetEntry(DatasetId)
            {
                Generation = Generation,
                StateRevision = StateRevision,
                CapturedAt = CapturedAt,
                LastAttemptAt = LastAttemptAt,
                LastSuccessAt = LastSuccessAt,
                ReadyUntil = ReadyUntil,
                FailureCode = FailureCode,
                FailureMessage = FailureMessage,
                FailureSignature = FailureSignature
            };
            foreach (var pair in Bars)
            {
                clone.Bars.Add(pair.Key, pair.Value);
            }
            foreach (var pair in Coverage)
            {
                clone.Coverage.Add(pair.Key, pair.Value);
            }
            return clone;
        }
    }

    private sealed record MetricCoverage(
        ProcessSampleDetailLevel ProcessDetailLevel);
}

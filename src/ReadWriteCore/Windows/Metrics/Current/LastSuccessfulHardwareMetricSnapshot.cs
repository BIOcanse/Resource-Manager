using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class LastSuccessfulHardwareMetricSnapshot
{
    private readonly string providerName;
    private readonly object sync = new();

    public LastSuccessfulHardwareMetricSnapshot(string providerName = "Metric snapshot")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        this.providerName = providerName;
    }
    private ulong nextHostedRunId;
    private ulong activeHostedRunId;
    private bool acceptingHostedPublications;

    public ulong OpenHostedRun()
    {
        lock (sync)
        {
            if (acceptingHostedPublications)
            {
                throw new InvalidOperationException(
                    "The hosted hardware publication run is already open.");
            }

            nextHostedRunId = checked(nextHostedRunId + 1);
            activeHostedRunId = nextHostedRunId;
            acceptingHostedPublications = true;
            return activeHostedRunId;
        }
    }

    public bool TryCaptureHostedTicket(
        ulong hostedRunId,
        IMetricPublicationPermit workspaceOwnerToken,
        ulong expectedWorkspaceIncarnation,
        ulong expectedConfigurationGeneration,
        out HardwareMetricHostedPublicationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(workspaceOwnerToken);
        lock (sync)
        {
            if (!IsCurrentHostedRunUnsafe(hostedRunId)
                || !workspaceOwnerToken.IsActive
                || expectedWorkspaceIncarnation == 0
                || expectedConfigurationGeneration == 0
                || workspaceOwnerToken.WorkspaceIncarnation !=
                    expectedWorkspaceIncarnation
                || workspaceOwnerToken.ConfigurationGeneration !=
                    expectedConfigurationGeneration)
            {
                ticket = default;
                return false;
            }

            ticket = new HardwareMetricHostedPublicationTicket(
                this,
                hostedRunId,
                workspaceOwnerToken);
            return true;
        }
    }

    public HardwareMetricSnapshot? Read()
        => Read(DateTimeOffset.UtcNow);

    /// <summary>Admits delivery of an unchanged current value without a new commit.</summary>
    public bool TryAdmitHostedDelivery(HardwareMetricHostedPublicationTicket ticket, Action deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        ValidateTicketOwner(ticket);
        var accepted = false;
        if (!ticket.WorkspaceOwnerToken.TryPublish(() =>
            {
                lock (sync)
                {
                    if (!CanCommitTicketUnsafe(ticket)) return;
                    deliver();
                    accepted = true;
                }
            })) return false;
        return accepted;
    }

    public HardwareMetricSnapshot? Read(DateTimeOffset now)
    {
        _ = now;
        lock (sync) return ReadSnapshot(ReadState());
    }

    // Direct SDK publications use the same managed current as hosted rounds.
    public void Publish(HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Datasets.Count != 0)
        {
            _ = Publish(snapshot, MetricSampleRequest.All);
            return;
        }
        lock (sync)
        {
            WriteState(new PublicationState(
                    new Dictionary<string, DatasetPublication>(
                        StringComparer.OrdinalIgnoreCase),
                    Freeze(snapshot), historyView, direct: true));
        }
    }

    public HardwareMetricPublicationResult Publish(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request)
        => Publish(snapshot, request, readyUntil: null);

    public HardwareMetricPublicationResult Publish(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        DateTimeOffset? readyUntil)
        => PublishUnsafe(
            snapshot,
            request,
            readyUntil,
            workspaceOwnerToken: null,
            requestedDatasetIds: null);

    private HardwareMetricPublicationResult PublishUnsafe(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        DateTimeOffset? readyUntil,
        IMetricPublicationPermit? workspaceOwnerToken,
        IReadOnlyCollection<string>? requestedDatasetIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        _ = readyUntil;
        var requested = requestedDatasetIds is null
            ? GetRequestedDatasets(snapshot, request)
            : MetricDatasetIds.ExpandBoundSystemDatasets(
                requestedDatasetIds, snapshot.FieldBindings.Values);
        // A source invalidation retires all of its bound slots, including those
        // outside this read request. Ordinary unavailable observations do not.
        if (!snapshot.InvalidatedDatasetIds.IsEmpty) requested.UnionWith(snapshot.InvalidatedDatasetIds);
        lock (sync)
        {
            var state = ReadState();
            snapshot = Freeze(snapshot, requested);
            var datasets = state.Datasets.ToBuilder();
            if (state.Context is { } prior)
            {
                // Independent slots survive a workspace replacement. Retire only
                // addresses whose physical device or field contract disappeared;
                // their display aliases cannot be reassigned the previous value.
                var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var binding in prior.FieldBindings.Values)
                {
                    // Managed control identities can change independently of a
                    // native workspace. Retire only those exact bound slots.
                    if (prior.WorkspaceIdentity == snapshot.WorkspaceIdentity && binding.ControlTarget is null) continue;
                    if (snapshot.FieldBindings.TryGetValue(binding.RequestAddress, out var next)
                        && binding.DeviceIdentity == next.DeviceIdentity && binding.Field.Name == next.Field.Name
                        && binding.Field.Unit == next.Field.Unit && binding.Field.ValueKind == next.Field.ValueKind
                        && binding.SourceId == next.SourceId && binding.AdapterKey == next.AdapterKey
                        && binding.ControlTarget == next.ControlTarget
                        && binding.EffectiveSampleRequestAddress == next.EffectiveSampleRequestAddress
                        && binding.PdhGpuEngineIdentity == next.PdhGpuEngineIdentity)
                        continue;
                    retired.Add(binding.RequestAddress);
                    foreach (var alias in binding.RequestAliases) retired.Add(MetricDatasetIds.ForSystemMetric(alias));
                }
                foreach (var datasetId in retired) datasets.Remove(datasetId);
            }
            var committed = new List<string>(requested.Count);
            var rejected = new List<string>(requested.Count);
            var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var datasetId in requested.Order(StringComparer.OrdinalIgnoreCase))
            {
                var invalidated = !snapshot.InvalidatedDatasetIds.IsEmpty && snapshot.InvalidatedDatasetIds.Contains(datasetId);
                var datasetSnapshot = Slice(snapshot, datasetId);
                if (!snapshot.Datasets.TryGetValue(datasetId, out var observation))
                {
                    datasets.TryGetValue(datasetId, out var previous);
                    if (invalidated) previous = null;
                    datasets[datasetId] = new DatasetPublication(
                        datasetId,
                        RetainSuccessfulObservation(CreateFailureObservation(
                            datasetId,
                            snapshot.CapturedAt,
                            "hardware-dataset-missing",
                            snapshot,
                            workspaceOwnerToken), previous),
                        previous?.PayloadSnapshot,
                        datasetSnapshot,
                        snapshot.CapturedAt);
                    rejected.Add(datasetId);
                    reasons.Add(datasetId, "hardware-dataset-missing");
                    continue;
                }

                if (IsPublishable(observation.Status) && !invalidated)
                {
                    var committedObservation = observation with
                    {
                        ReadyUntilUtcTicks = 0,
                        LastAttemptAtUtcTicks = observation.LastAttemptAtUtcTicks > 0
                            ? observation.LastAttemptAtUtcTicks
                            : snapshot.CapturedAt.UtcTicks,
                        LastSuccessAtUtcTicks = observation.Status ==
                                SamplingObservationStatus.Current
                            ? observation.LastSuccessAtUtcTicks > 0
                                ? observation.LastSuccessAtUtcTicks
                                : observation.ObservedAtUtcTicks > 0
                                    ? observation.ObservedAtUtcTicks
                                    : snapshot.CapturedAt.UtcTicks
                            : observation.LastSuccessAtUtcTicks,
                        FailureCode = observation.Status ==
                                SamplingObservationStatus.Current
                            ? null
                            : observation.FailureCode
                                ?? FailureCodeForStatus(observation.Status)
                    };
                    datasets[datasetId] = new DatasetPublication(
                        datasetId,
                        committedObservation,
                        datasetSnapshot,
                        datasetSnapshot,
                        new DateTimeOffset(committedObservation.LastAttemptAtUtcTicks, TimeSpan.Zero));
                    committed.Add(datasetId);
                }
                else
                {
                    datasets.TryGetValue(datasetId, out var previous);
                    if (invalidated) previous = null;
                    var failedObservation = CreateFailureObservation(
                        datasetId,
                        snapshot.CapturedAt,
                        observation.FailureCode ?? FailureCodeForStatus(observation.Status),
                        snapshot,
                        workspaceOwnerToken) with
                    {
                        // A failed candidate cannot introduce a value. The owner
                        // retains only its own previously successful slot.
                        Status = invalidated || observation.Status == SamplingObservationStatus.RetainedLastGood
                            ? SamplingObservationStatus.Unavailable : observation.Status,
                        LastAttemptAtUtcTicks = observation.LastAttemptAtUtcTicks > 0
                            ? observation.LastAttemptAtUtcTicks : snapshot.CapturedAt.UtcTicks
                    };
                    datasets[datasetId] = new DatasetPublication(
                        datasetId,
                        RetainSuccessfulObservation(failedObservation, previous),
                        previous?.PayloadSnapshot,
                        datasetSnapshot,
                        new DateTimeOffset(failedObservation.LastAttemptAtUtcTicks, TimeSpan.Zero));
                    rejected.Add(datasetId);
                    reasons.Add(datasetId, observation.FailureCode ?? FailureCodeForStatus(observation.Status));
                }
            }

            if (requested.Count != 0)
            {
                PublishHistory(datasets, requested);
                WriteState(new PublicationState(datasets.ToImmutable(),
                    snapshot, historyView));
            }

            var successful = requested.Count != 0 && rejected.Count == 0;
            return new HardwareMetricPublicationResult(
                successful,
                committed.ToArray(),
                rejected.ToArray()) { RejectionReasons = reasons };
        }
    }

    public bool TryPublishHosted(
        HardwareMetricHostedPublicationTicket ticket,
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        DateTimeOffset? readyUntil,
        out HardwareMetricPublicationResult result)
        => TryPublishHosted(
            ticket,
            snapshot,
            request,
            requestedDatasetIds: null,
            readyUntil: readyUntil,
            out result);

    public bool TryPublishHosted(
        HardwareMetricHostedPublicationTicket ticket,
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        IReadOnlyCollection<string>? requestedDatasetIds,
        DateTimeOffset? readyUntil,
        out HardwareMetricPublicationResult result)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ValidateTicketOwner(ticket);
        var accepted = false;
        var localResult = default(HardwareMetricPublicationResult);
        if (!ticket.WorkspaceOwnerToken.TryPublish(() =>
            {
                lock (sync)
                {
                    if (!CanCommitTicketUnsafe(ticket))
                    {
                        return;
                    }

                    localResult = PublishUnsafe(
                        snapshot,
                        request,
                        readyUntil,
                        ticket.WorkspaceOwnerToken,
                        requestedDatasetIds);
                    accepted = true;
                }
            }))
        {
            result = default;
            return false;
        }

        result = localResult;
        return accepted;
    }

    public bool TryRecordHostedFailure(
        HardwareMetricHostedPublicationTicket ticket,
        MetricSampleRequest request,
        DateTimeOffset attemptedAt,
        string failureCode)
        => TryRecordHostedFailure(
            ticket,
            request,
            requestedDatasetIds: null,
            attemptedAt: attemptedAt,
            failureCode: failureCode);

    public bool TryRecordHostedFailure(
        HardwareMetricHostedPublicationTicket ticket,
        MetricSampleRequest request,
        IReadOnlyCollection<string>? requestedDatasetIds,
        DateTimeOffset attemptedAt,
        string failureCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ValidateTicketOwner(ticket);
        var accepted = false;
        if (!ticket.WorkspaceOwnerToken.TryPublish(() =>
            {
                lock (sync)
                {
                    if (!CanCommitTicketUnsafe(ticket))
                    {
                        return;
                    }

                    RecordFailure(
                        request,
                        attemptedAt,
                        failureCode,
                        ticket.WorkspaceOwnerToken,
                        createMissingDatasets: true,
                        requestedDatasetIds);
                    accepted = true;
                }
            }))
        {
            return false;
        }

        return accepted;
    }

    public bool CloseHostedRun(
        ulong hostedRunId,
        DateTimeOffset closedAt,
        string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        lock (sync)
        {
            if (!IsCurrentHostedRunUnsafe(hostedRunId))
            {
                return false;
            }

            acceptingHostedPublications = false;
            _ = closedAt;
            return true;
        }
    }

    public void RecordFailure(
        MetricSampleRequest request,
        DateTimeOffset attemptedAt,
        string failureCode)
        => RecordFailure(
            request,
            attemptedAt,
            failureCode,
            workspaceOwnerToken: null,
            createMissingDatasets: true,
            requestedDatasetIds: null);

    private void RecordFailure(
        MetricSampleRequest request,
        DateTimeOffset attemptedAt,
        string failureCode,
        IMetricPublicationPermit? workspaceOwnerToken,
        bool createMissingDatasets,
        IReadOnlyCollection<string>? requestedDatasetIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        lock (sync)
        {
            var state = ReadState();
            var datasets = state.Datasets.ToBuilder();
            var requested = requestedDatasetIds is not null
                ? requestedDatasetIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : createMissingDatasets
                    ? MetricDatasetIds.ResolveFailedSystemDatasets(
                        request,
                        datasets.Keys)
                    : MetricDatasetIds.ResolveSystemDatasets(
                        request,
                        datasets.Keys);
            // Hosted failures use only bindings from their own workspace.
            var boundSnapshot = state.Context;
            var canUseBindings = boundSnapshot is not null && (workspaceOwnerToken is null
                || boundSnapshot.WorkspaceIdentity == workspaceOwnerToken.WorkspaceIncarnation
                    && boundSnapshot.ConfigurationGeneration == workspaceOwnerToken.ConfigurationGeneration);
            requested = MetricDatasetIds.ExpandBoundSystemDatasets(requested,
                canUseBindings ? boundSnapshot!.FieldBindings.Values : []);
            if (requested.Count == 0)
            {
                return;
            }

            var attemptSnapshot = CreateFailureAttemptSnapshot(
                attemptedAt,
                requested,
                failureCode,
                workspaceOwnerToken,
                state.Context);
            var changed = false;
            foreach (var datasetId in requested)
            {
                datasets.TryGetValue(datasetId, out var previous);
                datasets[datasetId] = new DatasetPublication(
                    datasetId,
                    RetainSuccessfulObservation(CreateFailureObservation(
                        datasetId,
                        attemptedAt,
                        failureCode,
                        attemptSnapshot,
                        workspaceOwnerToken), previous),
                    previous?.PayloadSnapshot,
                    attemptSnapshot,
                    attemptedAt);
                changed = true;
            }

            if (changed)
            {
                // No native frame completed, so no catalog was published by this
                // failure. Keep the admitted publication's catalog identity.
                PublishHistory(datasets, requested);
                WriteState(new PublicationState(datasets.ToImmutable(),
                    state.Context ?? Freeze(attemptSnapshot), historyView));
            }
        }
    }

    private static bool IsPublishable(SamplingObservationStatus status)
        => status == SamplingObservationStatus.Current;

    private static HardwareMetricDatasetObservation RetainSuccessfulObservation(
        HardwareMetricDatasetObservation failure, DatasetPublication? previous)
        => previous?.PayloadSnapshot is null ? failure : previous.Observation with
        {
            Status = failure.Status,
            LastAttemptAtUtcTicks = failure.LastAttemptAtUtcTicks,
            ReadyUntilUtcTicks = 0,
            FailureCode = failure.FailureCode
        };

    private HardwareMetricSnapshot CreateFailureAttemptSnapshot(
        DateTimeOffset attemptedAt,
        IReadOnlySet<string> datasetIds,
        string failureCode,
        IMetricPublicationPermit? workspaceOwnerToken,
        HardwareMetricSnapshot? catalogSnapshot)
    {
        var workspaceIdentity = workspaceOwnerToken?.WorkspaceIncarnation ?? 0;
        var configurationGeneration =
            workspaceOwnerToken?.ConfigurationGeneration ?? 0;
        var observations = datasetIds.ToDictionary(
            static datasetId => datasetId,
            datasetId => new HardwareMetricDatasetObservation(
                datasetId,
                SamplingObservationStatus.Unavailable,
                0,
                0,
                workspaceIdentity,
                configurationGeneration,
                0,
                0)
            {
                LastAttemptAtUtcTicks = attemptedAt.UtcTicks,
                FailureCode = failureCode
            },
            StringComparer.OrdinalIgnoreCase);
        var gpuRequested = datasetIds.Contains(
                MetricDatasetIds.SystemGpuInventory)
            || datasetIds.Any(static datasetId =>
                datasetId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase));
        return new HardwareMetricSnapshot(
            DateTimeOffset.UnixEpoch,
            new CpuMetrics(
                string.Empty,
                0,
                false,
                datasetIds.Contains(MetricDatasetIds.SystemCpuUsage)
                    ? CpuMetricObservationStatus.Unavailable
                    : CpuMetricObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                new CpuSensorMetrics(
                    new HardwareSensorProviderState(
                        providerName,
                        HardwareSensorProviderStates.Unavailable,
                        failureCode),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false)
            {
                ObservationStatus = datasetIds.Contains(
                    MetricDatasetIds.SystemMemoryUsage)
                    ? SamplingObservationStatus.Unavailable
                    : SamplingObservationStatus.NotRequested
            },
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false)
            {
                ObservationStatus = datasetIds.Contains(
                    MetricDatasetIds.SystemVirtualMemoryUsage)
                    ? SamplingObservationStatus.Unavailable
                    : SamplingObservationStatus.NotRequested
            },
            catalogSnapshot?.Gpus.Select(gpu => EmptyGpu(new(gpu.Index, gpu.Name, gpu.IdentityKey,
                gpu.AdapterKey, gpu.UsageProvider))).ToArray() ?? [],
            new SchedulingGpuInventorySnapshot(
                gpuRequested
                    ? SamplingObservationStatus.Unavailable
                    : SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricReadingValue>(
                StringComparer.OrdinalIgnoreCase))
        {
            Datasets = observations,
            WorkspaceIdentity = workspaceIdentity,
            ConfigurationGeneration = configurationGeneration,
            FieldBindings = catalogSnapshot is not null && (workspaceOwnerToken is null
                || catalogSnapshot.WorkspaceIdentity == workspaceIdentity
                    && catalogSnapshot.ConfigurationGeneration == configurationGeneration)
                ? catalogSnapshot.FieldBindings : new Dictionary<string, MetricDeviceFieldBinding>()
        };
    }

    private static HardwareMetricDatasetObservation CreateFailureObservation(
        string datasetId,
        DateTimeOffset attemptedAt,
        string failureCode,
        HardwareMetricSnapshot attemptSnapshot,
        IMetricPublicationPermit? workspaceOwnerToken)
    {
        return new HardwareMetricDatasetObservation(
            datasetId,
            SamplingObservationStatus.Unavailable,
            0,
            0,
            attemptSnapshot.WorkspaceIdentity != 0
                ? attemptSnapshot.WorkspaceIdentity
                : workspaceOwnerToken?.WorkspaceIncarnation ?? 0,
            attemptSnapshot.ConfigurationGeneration != 0
                ? attemptSnapshot.ConfigurationGeneration
                : workspaceOwnerToken?.ConfigurationGeneration ?? 0,
            0,
            0)
        {
            LastAttemptAtUtcTicks = attemptedAt.UtcTicks,
            LastSuccessAtUtcTicks = 0,
            ReadyUntilUtcTicks = 0,
            FailureCode = failureCode
        };
    }

    private static string FailureCodeForStatus(SamplingObservationStatus status)
        => status switch
        {
            SamplingObservationStatus.Unavailable =>
                "hardware-dataset-unavailable",
            SamplingObservationStatus.NotRequested =>
                "hardware-dataset-not-requested",
            SamplingObservationStatus.Unsupported =>
                "hardware-dataset-unsupported",
            SamplingObservationStatus.Partial =>
                "hardware-dataset-partial",
            SamplingObservationStatus.Warming =>
                "hardware-dataset-warming",
            SamplingObservationStatus.Invalid =>
                "hardware-dataset-invalid",
            _ => "hardware-dataset-non-current"
        };

    private static HashSet<string> GetRequestedDatasets(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request)
        => MetricDatasetIds.ExpandBoundSystemDatasets(
            MetricDatasetIds.ResolveSystemDatasets(request, snapshot.Datasets.Keys),
            snapshot.FieldBindings.Values);

    private HardwareMetricSnapshot Compose(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        HardwareMetricSnapshot catalogPublication)
    {
        var latestSuccessAtUtcTicks = datasets.Values
            .Where(static publication => publication.PayloadSnapshot is not null)
            .Select(static publication =>
                publication.Observation.LastSuccessAtUtcTicks > 0
                    ? publication.Observation.LastSuccessAtUtcTicks
                    : publication.Observation.ObservedAtUtcTicks)
            .Where(static ticks => ticks > 0)
            .DefaultIfEmpty(0)
            .Max();
        var items = new Dictionary<string, MetricReadingValue>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var publication in datasets.Values)
        {
            if (publication.PayloadSnapshot is not { } payload)
            {
                continue;
            }
            foreach (var (id, value) in payload.Items)
            {
                if (string.Equals(
                        MetricDatasetIds.ForSystemMetric(id),
                        publication.DatasetId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    items[id] = value;
                }
            }
        }

        var cpu = ComposeCpu(datasets, catalogPublication);
        var memory = TryGetPublication(
            datasets,
            MetricDatasetIds.SystemMemoryUsage,
            out var memoryPublication)
            && memoryPublication.PayloadSnapshot is { } memorySnapshot
            ? memorySnapshot.Memory with
            {
                ObservationStatus = memoryPublication.Observation.Status
            }
            : new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false)
            {
                ObservationStatus = memoryPublication is null
                    ? SamplingObservationStatus.NotRequested
                    : SamplingObservationStatus.Unavailable
            };
        var virtualMemory = TryGetPublication(
            datasets,
            MetricDatasetIds.SystemVirtualMemoryUsage,
            out var virtualPublication)
            && virtualPublication.PayloadSnapshot is { } virtualSnapshot
            ? virtualSnapshot.VirtualMemory with
            {
                ObservationStatus = virtualPublication.Observation.Status
            }
            : new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false)
            {
                ObservationStatus = virtualPublication is null
                    ? SamplingObservationStatus.NotRequested
                    : SamplingObservationStatus.Unavailable
            };
        var gpus = ComposeGpus(datasets, catalogPublication);
        var inventory = ComposeGpuInventory(datasets, gpus);
        var observations = datasets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Observation,
            StringComparer.OrdinalIgnoreCase);

        return new HardwareMetricSnapshot(
            latestSuccessAtUtcTicks > 0
                ? new DateTimeOffset(latestSuccessAtUtcTicks, TimeSpan.Zero)
                : DateTimeOffset.UnixEpoch,
            cpu,
            memory,
            virtualMemory,
            gpus.ToImmutableArray(),
            inventory with { Adapters = inventory.Adapters.ToImmutableArray() },
            items.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase))
        {
            Datasets = observations.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            WorkspaceIdentity = catalogPublication.WorkspaceIdentity,
            ConfigurationGeneration = catalogPublication.ConfigurationGeneration,
            CatalogGeneration = catalogPublication.CatalogGeneration,
            CommittedGeneration = catalogPublication.CommittedGeneration,
            FieldBindings = catalogPublication.FieldBindings
        };
    }

    private CpuMetrics ComposeCpu(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        HardwareMetricSnapshot catalogPublication)
    {
        var usageId = MetricDatasetIds.SystemCpuUsage;
        var frequencyId = MetricDatasetIds.ForSystemMetric("cpu.frequency");
        var frequencyPercentId = MetricDatasetIds.ForSystemMetric(
            "cpu.frequencyPercent");
        var hasUsage = TryGetSnapshot(datasets, usageId, out var usage);
        var hasFrequency = TryGetSnapshot(datasets, frequencyId, out var frequency);
        var hasFrequencyPercent = TryGetSnapshot(
            datasets,
            frequencyPercentId,
            out var frequencyPercent);
        var sensors = new CpuSensorMetrics(
            new HardwareSensorProviderState(
                providerName,
                HardwareSensorProviderStates.Unavailable,
                null),
            CpuSensor(datasets, "cpu.packagePower", static value => value.PackagePowerWatts),
            CpuSensor(datasets, "cpu.coreVoltage", static value => value.CoreVoltageVolts),
            CpuSensor(datasets, "cpu.packageCurrent", static value => value.PackageCurrentAmps),
            CpuSensor(datasets, "cpu.temperature", static value => value.TemperatureCelsius),
            CpuSensor(datasets, "cpu.stapmPower", static value => value.StapmPowerWatts),
            CpuSensor(datasets, "cpu.actualPower", static value => value.ActualPowerWatts),
            CpuSensor(datasets, "cpu.averagePower", static value => value.AveragePowerWatts),
            CpuSensor(datasets, "cpu.tdcCurrent", static value => value.TdcCurrentAmps),
            CpuSensor(datasets, "cpu.edcCurrent", static value => value.EdcCurrentAmps),
            CpuSensor(datasets, "cpu.platformPower", static value => value.SocPowerWatts),
            CpuSensor(datasets, "cpu.platformVoltage", static value => value.SocVoltageVolts),
            CpuSensor(datasets, "cpu.igpuFrequency", static value => value.ApuFrequencyMhz),
            CpuSensor(datasets, "cpu.igpuVoltage", static value => value.ApuVoltageVolts),
            CpuSensor(datasets, "cpu.igpuTemperature", static value => value.ApuTemperatureCelsius),
            CpuSensor(datasets, "cpu.smuFrequency", static value => value.SmuFrequencyMhz));
        var usageCurrent = hasUsage
            && datasets[usageId].Observation.Status == SamplingObservationStatus.Current;
        return new CpuMetrics(
            catalogPublication.Cpu.Name,
            hasUsage ? usage.Cpu.UsagePercent : 0,
            usageCurrent && usage.Cpu.IsUsageAvailable,
            usageCurrent
                ? usage.Cpu.ObservationStatus
                : hasUsage
                    ? CpuMetricObservationStatus.Unavailable
                    : datasets.ContainsKey(usageId)
                        ? CpuMetricObservationStatus.Unavailable
                        : CpuMetricObservationStatus.NotRequested,
            hasUsage ? datasets[usageId].Observation.SourceGeneration : 0,
            hasUsage ? usage.Cpu.SampleDurationMilliseconds : 0,
            hasFrequency ? frequency.Cpu.CurrentFrequencyMhz : 0,
            // 参考频率是这台机器的**固有属性**，不是一次采样的结果 ——
            // 没人在采频率的时候它也还是那个数。先前这里没采到就归零，
            // 于是目录里的说明写成"参考 0 MHz"。
            hasFrequency ? frequency.Cpu.MaxFrequencyMhz : catalogPublication.Cpu.MaxFrequencyMhz,
            hasFrequencyPercent ? frequencyPercent.Cpu.FrequencyPercent : 0,
            hasFrequency ? frequency.Cpu.FrequencySource : catalogPublication.Cpu.FrequencySource,
            sensors);
    }

    private static double? CpuSensor(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        string metricId,
        Func<CpuSensorMetrics, double?> selector)
        => TryGetSnapshot(
                datasets,
                MetricDatasetIds.ForSystemMetric(metricId),
                out var snapshot)
            ? selector(snapshot.Cpu.Sensors)
            : null;

    private IReadOnlyList<GpuMetrics> ComposeGpus(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        HardwareMetricSnapshot catalogPublication)
    {
        var result = new List<GpuMetrics>(catalogPublication.Gpus.Count);
        foreach (var descriptor in catalogPublication.Gpus.OrderBy(static gpu => gpu.Index))
        {
            var index = descriptor.Index;
            var usage = GpuSnapshot(datasets, index, "usage");
            var vram = GpuSnapshot(datasets, index, "vram");
            var graphicsClock = GpuSnapshot(datasets, index, "graphicsClock");
            var graphicsPercent = GpuSnapshot(datasets, index, "graphicsClockPercent");
            var memoryClock = GpuSnapshot(datasets, index, "memoryClock");
            result.Add(new GpuMetrics(
                index,
                descriptor.Name,
                usage?.UsagePercent ?? 0,
                graphicsClock?.GraphicsFrequencyMhz ?? 0,
                graphicsClock?.StandardGraphicsFrequencyMhz ?? 0,
                graphicsPercent?.GraphicsFrequencyPercent ?? 0,
                memoryClock?.MemoryFrequencyMhz ?? 0,
                vram?.UsedMemoryBytes ?? 0,
                vram?.TotalMemoryBytes ?? 0,
                vram?.MemoryUsagePercent ?? 0,
                ComposeGpuSensors(datasets, index),
                usage?.UsageProvider ?? string.Empty,
                usage?.IsUsageAvailable == true
                    && IsCurrent(
                        datasets,
                        MetricDatasetIds.ForSystemMetric(
                            $"gpu.{index}.usage")),
                descriptor.IdentityKey) { HasDedicatedMemoryMetrics = descriptor.HasDedicatedMemoryMetrics });
        }
        return result;
    }

    private GpuSensorMetrics ComposeGpuSensors(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        int index)
        => new(
            new HardwareSensorProviderState(
                providerName,
                HardwareSensorProviderStates.Unavailable,
                null),
            GpuSensor(datasets, index, "power", static value => value.PowerWatts),
            GpuSensor(datasets, index, "powerLimit", static value => value.PowerLimitWatts),
            GpuSensor(datasets, index, "boardPower", static value => value.BoardPowerWatts),
            GpuSensor(datasets, index, "temperature", static value => value.TemperatureCelsius),
            GpuSensor(datasets, index, "hotspotTemperature", static value => value.HotspotTemperatureCelsius),
            GpuSensor(datasets, index, "intakeTemperature", static value => value.IntakeTemperatureCelsius),
            GpuSensor(datasets, index, "fanPercent", static value => value.FanSpeedPercent),
            GpuSensor(datasets, index, "fanRpm", static value => value.FanSpeedRpm),
            GpuSensor(datasets, index, "coreVoltage", static value => value.CoreVoltageVolts),
            GpuSensor(datasets, index, "current", static value => value.CurrentAmps));

    private static double? GpuSensor(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        int index,
        string metricName,
        Func<GpuSensorMetrics, double?> selector)
        => GpuSnapshot(datasets, index, metricName) is { } gpu
            ? selector(gpu.Sensors)
            : null;

    private static GpuMetrics? GpuSnapshot(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        int index,
        string metricName)
    {
        var datasetId = MetricDatasetIds.ForSystemMetric(
            $"gpu.{index}.{metricName}");
        return TryGetSnapshot(datasets, datasetId, out var snapshot)
            ? snapshot.Gpus.FirstOrDefault(gpu => gpu.Index == index)
            : null;
    }

    private static SchedulingGpuInventorySnapshot ComposeGpuInventory(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        IReadOnlyList<GpuMetrics> gpus)
    {
        if (!datasets.TryGetValue(
                MetricDatasetIds.SystemGpuInventory,
                out var inventoryPublication))
        {
            return new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []);
        }

        var source = inventoryPublication.PayloadSnapshot?.GpuInventory
            ?? inventoryPublication.AttemptSnapshot.GpuInventory;
        var rows = source.Adapters.Select(adapter =>
        {
            var gpu = gpus.FirstOrDefault(value => value.Index == adapter.Index);
            var usageDatasetId = MetricDatasetIds.ForSystemMetric(
                $"gpu.{adapter.Index}.usage");
            var capacityDatasetId = MetricDatasetIds.ForSystemMetric(
                $"gpu.{adapter.Index}.vram");
            var usageCurrent = datasets.TryGetValue(
                usageDatasetId,
                out var usagePublication)
                && usagePublication.Observation.Status == SamplingObservationStatus.Current
                && HasCompatibleGpuTopology(usagePublication, source);
            var capacityCurrent = datasets.TryGetValue(
                capacityDatasetId,
                out var capacityPublication)
                && capacityPublication.Observation.Status == SamplingObservationStatus.Current
                && HasCompatibleGpuTopology(capacityPublication, source);
            var valid = SchedulingGpuMetricMask.None;
            if (usageCurrent)
            {
                valid |= SchedulingGpuMetricMask.Usage;
            }
            if (capacityCurrent)
            {
                valid |= SchedulingGpuMetricMask.UsedDedicatedMemory
                    | SchedulingGpuMetricMask.TotalDedicatedMemory;
            }
            return adapter with
            {
                ValidMetricMask = valid,
                UsageStatus = usageCurrent
                    ? usagePublication!.Observation.Status
                    : SamplingObservationStatus.NotRequested,
                CapacityStatus = capacityCurrent
                    ? capacityPublication!.Observation.Status
                    : SamplingObservationStatus.NotRequested,
                UsagePercent = gpu?.UsagePercent ?? 0,
                UsedDedicatedMemoryBytes = gpu?.UsedMemoryBytes ?? 0,
                TotalDedicatedMemoryBytes = gpu?.TotalMemoryBytes ?? 0,
                Generation = source.Generation,
                ObservedAtUtcTicks = source.ObservedAtUtcTicks
            };
        }).ToArray();
        var complete = inventoryPublication.Observation.Status == SamplingObservationStatus.Current;
        return source with
        {
            Status = source.Status == SamplingObservationStatus.Unsupported
                ? SamplingObservationStatus.Unsupported
                : complete
                    ? SamplingObservationStatus.Current
                    : SamplingObservationStatus.Partial,
            Adapters = rows
        };
    }

    private static bool HasCompatibleGpuTopology(
        DatasetPublication publication,
        SchedulingGpuInventorySnapshot inventory)
        => publication.PayloadSnapshot is { } snapshot
            && snapshot.GpuInventory.TopologyFingerprint != 0
            && snapshot.GpuInventory.TopologyFingerprint ==
                inventory.TopologyFingerprint;

    private static bool TryGetSnapshot(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        string datasetId,
        out HardwareMetricSnapshot snapshot)
    {
        if (datasets.TryGetValue(datasetId, out var publication)
            && publication.PayloadSnapshot is { } payload)
        {
            snapshot = payload;
            return true;
        }
        snapshot = null!;
        return false;
    }

    private static bool TryGetPublication(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        string datasetId,
        out DatasetPublication publication)
        => datasets.TryGetValue(datasetId, out publication!);

    private static bool IsCurrent(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        string datasetId)
        => datasets.TryGetValue(datasetId, out var publication)
            && publication.Observation.Status == SamplingObservationStatus.Current;

    private void ValidateTicketOwner(
        HardwareMetricHostedPublicationTicket ticket)
    {
        if (!ReferenceEquals(ticket.Owner, this))
        {
            throw new InvalidOperationException(
                "The hosted hardware publication ticket belongs to another state.");
        }
    }

    private bool CanCommitTicketUnsafe(
        HardwareMetricHostedPublicationTicket ticket)
        => IsCurrentHostedRunUnsafe(ticket.HostedRunId);

    private bool IsCurrentHostedRunUnsafe(ulong hostedRunId)
        => acceptingHostedPublications
            && hostedRunId != 0
            && hostedRunId == activeHostedRunId;

    private sealed record DatasetPublication(
        string DatasetId,
        HardwareMetricDatasetObservation Observation,
        HardwareMetricSnapshot? PayloadSnapshot,
        HardwareMetricSnapshot AttemptSnapshot,
        DateTimeOffset LastAttemptAt);

}

public readonly record struct HardwareMetricHostedPublicationTicket(
    LastSuccessfulHardwareMetricSnapshot Owner,
    ulong HostedRunId,
    IMetricPublicationPermit WorkspaceOwnerToken);

public readonly record struct HardwareMetricPublicationResult(
    bool IsSuccessful,
    IReadOnlyList<string> CommittedDatasetIds,
    IReadOnlyList<string> RejectedDatasetIds)
{
    public IReadOnlyDictionary<string, string> RejectionReasons { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int RequestedDatasetCount =>
        checked(CommittedDatasetIds.Count + RejectedDatasetIds.Count);

    public int CommittedDatasetCount => CommittedDatasetIds.Count;

    public int RejectedDatasetCount => RejectedDatasetIds.Count;
}

using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

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
    private PublicationState state = PublicationState.Empty;
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

    public HardwareMetricSnapshot? Read(DateTimeOffset now)
    {
        _ = now;
        return Volatile.Read(ref state).Snapshot;
    }

    // Catalog probes are isolated from the hosted observation cache and retain
    // their exact full projection.
    public void Publish(HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            Volatile.Write(
                ref state,
                new PublicationState(
                    new Dictionary<string, DatasetPublication>(
                        StringComparer.OrdinalIgnoreCase),
                    snapshot));
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
        lock (sync)
        {
            var datasets = new Dictionary<string, DatasetPublication>(
                state.Datasets,
                StringComparer.OrdinalIgnoreCase);
            if (state.Snapshot is { } prior)
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
            foreach (var datasetId in requested.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!snapshot.Datasets.TryGetValue(datasetId, out var observation))
                {
                    datasets[datasetId] = new DatasetPublication(
                        datasetId,
                        CreateFailureObservation(
                            datasetId,
                            snapshot.CapturedAt,
                            "hardware-dataset-missing",
                            snapshot,
                            workspaceOwnerToken),
                        null,
                        snapshot,
                        snapshot.CapturedAt);
                    rejected.Add(datasetId);
                    continue;
                }

                if (IsPublishable(observation.Status))
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
                        snapshot,
                        snapshot,
                        snapshot.CapturedAt);
                    committed.Add(datasetId);
                }
                else
                {
                    datasets[datasetId] = new DatasetPublication(
                        datasetId,
                        CreateFailureObservation(
                            datasetId,
                            snapshot.CapturedAt,
                            observation.FailureCode
                                ?? FailureCodeForStatus(observation.Status),
                            snapshot,
                            workspaceOwnerToken),
                        null,
                        snapshot,
                        snapshot.CapturedAt);
                    rejected.Add(datasetId);
                }
            }

            if (requested.Count != 0)
            {
                var snapshotWithHistory = PublishHistory(Compose(datasets), datasets, requested);
                Volatile.Write(
                    ref state,
                    new PublicationState(datasets, snapshotWithHistory));
            }

            var successful = requested.Count != 0 && rejected.Count == 0;
            return new HardwareMetricPublicationResult(
                successful,
                committed.ToArray(),
                rejected.ToArray());
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
            var datasets = new Dictionary<string, DatasetPublication>(
                state.Datasets,
                StringComparer.OrdinalIgnoreCase);
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
            var boundSnapshot = state.Snapshot;
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
                state.Snapshot);
            var changed = false;
            foreach (var datasetId in requested)
            {
                datasets[datasetId] = new DatasetPublication(
                    datasetId,
                    CreateFailureObservation(
                        datasetId,
                        attemptedAt,
                        failureCode,
                        attemptSnapshot,
                        workspaceOwnerToken),
                    null,
                    attemptSnapshot,
                    attemptedAt);
                changed = true;
            }

            if (changed)
            {
                var snapshotWithHistory = PublishHistory(Compose(datasets), datasets, requested);
                Volatile.Write(
                    ref state,
                    new PublicationState(datasets, snapshotWithHistory));
            }
        }
    }

    private static bool IsPublishable(SamplingObservationStatus status)
        => status == SamplingObservationStatus.Current;

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
                        "Unavailable",
                        failureCode),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(0, 0, 0, false, string.Empty)
            {
                ObservationStatus = datasetIds.Contains(
                    MetricDatasetIds.SystemMemoryUsage)
                    ? SamplingObservationStatus.Unavailable
                    : SamplingObservationStatus.NotRequested
            },
            new VirtualMemoryMetrics(0, 0, 0, string.Empty, false)
            {
                ObservationStatus = datasetIds.Contains(
                    MetricDatasetIds.SystemVirtualMemoryUsage)
                    ? SamplingObservationStatus.Unavailable
                    : SamplingObservationStatus.NotRequested
            },
            [],
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
            new Dictionary<string, MetricValue>(
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
        IReadOnlyDictionary<string, DatasetPublication> datasets)
    {
        var latest = datasets.Values
            .OrderByDescending(static publication => publication.LastAttemptAt)
            .ThenByDescending(static publication => publication.Observation.CommittedGeneration)
            .First();
        var latestSuccessAtUtcTicks = datasets.Values
            .Where(static publication => publication.PayloadSnapshot is not null)
            .Select(static publication =>
                publication.Observation.LastSuccessAtUtcTicks > 0
                    ? publication.Observation.LastSuccessAtUtcTicks
                    : publication.Observation.ObservedAtUtcTicks)
            .Where(static ticks => ticks > 0)
            .DefaultIfEmpty(0)
            .Max();
        var items = new Dictionary<string, MetricValue>(
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

        var cpu = ComposeCpu(datasets, latest.AttemptSnapshot);
        var memory = TryGetPublication(
            datasets,
            MetricDatasetIds.SystemMemoryUsage,
            out var memoryPublication)
            && memoryPublication.PayloadSnapshot is { } memorySnapshot
            ? memorySnapshot.Memory with
            {
                ObservationStatus = memoryPublication.Observation.Status
            }
            : new MemoryMetrics(0, 0, 0, false, latest.AttemptSnapshot.Memory.HardwareDescription)
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
            : new VirtualMemoryMetrics(0, 0, 0, "Windows commit", false)
            {
                ObservationStatus = virtualPublication is null
                    ? SamplingObservationStatus.NotRequested
                    : SamplingObservationStatus.Unavailable
            };
        var gpus = ComposeGpus(datasets);
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
            gpus,
            inventory,
            items)
        {
            Datasets = observations,
            WorkspaceIdentity = latest.Observation.WorkspaceIdentity,
            ConfigurationGeneration = latest.Observation.ConfigurationGeneration,
            CatalogGeneration = latest.Observation.CatalogGeneration,
            CommittedGeneration = latest.Observation.CommittedGeneration,
            FieldBindings = latest.AttemptSnapshot.FieldBindings
        };
    }

    private CpuMetrics ComposeCpu(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        HardwareMetricSnapshot latest)
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
                "Unavailable",
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
        var source = hasUsage ? usage : latest;
        var usageCurrent = hasUsage
            && datasets[usageId].Observation.Status == SamplingObservationStatus.Current;
        return new CpuMetrics(
            source.Cpu.Name,
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
            hasFrequency ? frequency.Cpu.MaxFrequencyMhz : latest.Cpu.MaxFrequencyMhz,
            hasFrequencyPercent ? frequencyPercent.Cpu.FrequencyPercent : 0,
            hasFrequency ? frequency.Cpu.FrequencySource : latest.Cpu.FrequencySource,
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
        IReadOnlyDictionary<string, DatasetPublication> datasets)
    {
        var indexes = datasets.Values
            .Where(static publication => publication.PayloadSnapshot is not null)
            .SelectMany(static publication => publication.PayloadSnapshot!.Gpus)
            .Select(static gpu => gpu.Index)
            .Distinct()
            .Order()
            .ToArray();
        var result = new List<GpuMetrics>(indexes.Length);
        foreach (var index in indexes)
        {
            var latest = datasets.Values
                .Where(static publication => publication.PayloadSnapshot is not null)
                .OrderBy(static publication => publication.LastAttemptAt)
                .Select(publication => publication.PayloadSnapshot!.Gpus
                    .FirstOrDefault(gpu => gpu.Index == index))
                .Where(static gpu => gpu is not null)
                .Last()!;
            var usage = GpuSnapshot(datasets, index, "usage");
            var vram = GpuSnapshot(datasets, index, "vram");
            var graphicsClock = GpuSnapshot(datasets, index, "graphicsClock");
            var graphicsPercent = GpuSnapshot(datasets, index, "graphicsClockPercent");
            var memoryClock = GpuSnapshot(datasets, index, "memoryClock");
            result.Add(new GpuMetrics(
                index,
                latest.Name,
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
                latest.IdentityKey));
        }
        return result;
    }

    private GpuSensorMetrics ComposeGpuSensors(
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        int index)
        => new(
            new HardwareSensorProviderState(
                providerName,
                "Unavailable",
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
        var complete = inventoryPublication.Observation.Status ==
                SamplingObservationStatus.Current
            && rows.All(static adapter =>
                (!adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.Usage)
                    || adapter.UsageStatus == SamplingObservationStatus.Current)
                && (!adapter.CapabilityMask.HasFlag(
                        SchedulingGpuCapabilityMask.DedicatedMemory)
                    || adapter.CapacityStatus == SamplingObservationStatus.Current));
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

    private sealed record PublicationState(
        IReadOnlyDictionary<string, DatasetPublication> Datasets,
        HardwareMetricSnapshot? Snapshot)
    {
        internal static PublicationState Empty { get; } = new(
            new Dictionary<string, DatasetPublication>(
                StringComparer.OrdinalIgnoreCase),
            null);
    }
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
    public int RequestedDatasetCount =>
        checked(CommittedDatasetIds.Count + RejectedDatasetIds.Count);

    public int CommittedDatasetCount => CommittedDatasetIds.Count;

    public int RejectedDatasetCount => RejectedDatasetIds.Count;
}

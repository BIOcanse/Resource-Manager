using System.Runtime.InteropServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativePdhCollector : INativePdhSnapshotSource, IDisposable
{
    private static readonly TimeSpan DefaultFrameReuseWindow = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan DefaultLastGoodLifetime = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly INativePdhApi api;
    private readonly HostManagerPdhCollectorRuntime? runtime;
    private readonly TimeProvider timeProvider;
    private TimeSpan frameReuseWindow;
    private TimeSpan lastGoodLifetime;
    private PdhCollectorRuntimePlan? createdPlan;
    private PdhCollectorRuntimePlan? appliedPlan;
    private HostManagerDeploymentAttemptToken? activeDeploymentAttempt;
    private IntPtr handle;
    private NativePdhSnapshot? lastGood;
    private NativePdhReadResult lastRead;
    private long lastReadTimestamp;
    private long gpuEngineLastGoodTimestamp;
    private long gpuMemoryLastGoodTimestamp;
    private long diskLastGoodTimestamp;
    private long networkLastGoodTimestamp;
    private bool hasLastReadTimestamp;
    private bool hasGpuEngineLastGood;
    private bool hasGpuMemoryLastGood;
    private bool hasDiskLastGood;
    private bool hasNetworkLastGood;
    private ulong adapterTopologyFingerprint;
    private bool hasAdapterTopologyFingerprint;
    private bool initializationAttempted;
    private bool permanentlyUnavailable;
    private bool disposed;
    private long failedRecreateRuntimePlanVersion = -1;

    internal NativePdhCollector(HostManagerPdhCollectorRuntime runtime)
        : this(new NativePdhApi(), DefaultFrameReuseWindow, TimeProvider.System, DefaultLastGoodLifetime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    internal NativePdhCollector(
        HostManagerPdhCollectorRuntime runtime,
        INativePdhApi api,
        TimeSpan frameReuseWindow,
        TimeProvider timeProvider,
        TimeSpan lastGoodLifetime)
        : this(api, frameReuseWindow, timeProvider, lastGoodLifetime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    internal NativePdhCollector(INativePdhApi api, TimeSpan frameReuseWindow)
        : this(api, frameReuseWindow, TimeProvider.System, DefaultLastGoodLifetime)
    {
    }

    internal NativePdhCollector(
        INativePdhApi api,
        TimeSpan frameReuseWindow,
        TimeProvider timeProvider,
        TimeSpan lastGoodLifetime)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (lastGoodLifetime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lastGoodLifetime));
        }

        this.api = api;
        this.frameReuseWindow = frameReuseWindow < TimeSpan.Zero ? TimeSpan.Zero : frameReuseWindow;
        this.timeProvider = timeProvider;
        this.lastGoodLifetime = lastGoodLifetime;
        lastRead = CreateUnavailable(NativePdhResultCode.Unavailable, 0);
    }

    public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
    {
        lock (gate)
        {
            var plan = CaptureAppliedPlan();
            if (disposed || permanentlyUnavailable)
            {
                return lastRead = CreateFallback(NativePdhResultCode.Unavailable, 0);
            }

            if (!EnsureInitialized(plan))
            {
                return lastRead = CreateFallback(NativePdhResultCode.Unavailable, 0);
            }

            var topologyChanged = UpdateAdapterTopology(adapterTopologyFingerprint);
            if (topologyChanged)
            {
                ClearGpuDatasetState();
                try
                {
                    var refreshResult = api.RequestTopologyRefresh(handle);
                    if (refreshResult != NativePdhResultCode.Ok)
                    {
                        return lastRead = CreateFallback(refreshResult, 0);
                    }
                }
                catch (Exception exception) when (IsInteropUnavailable(exception))
                {
                    permanentlyUnavailable = true;
                    CompleteDeploymentFailure("pdh-collector-topology-refresh-unavailable");
                    return lastRead = CreateFallback(NativePdhResultCode.Unavailable, 0);
                }
                catch
                {
                    CompleteDeploymentFailure("pdh-collector-topology-refresh-failed");
                    throw;
                }
            }

            var now = timeProvider.GetTimestamp();
            if (!topologyChanged
                && hasLastReadTimestamp
                && timeProvider.GetElapsedTime(lastReadTimestamp, now) < frameReuseWindow)
            {
                return lastRead;
            }

            EnsureDeploymentAttempt(plan);
            lastReadTimestamp = now;
            hasLastReadTimestamp = true;
            var header = new NativePdhFrameHeader
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = (uint)Marshal.SizeOf<NativePdhFrameHeader>()
            };

            NativePdhResultCode sampleResult;
            try
            {
                sampleResult = api.Sample(handle, ref header);
            }
            catch (Exception exception) when (IsInteropUnavailable(exception))
            {
                permanentlyUnavailable = true;
                CompleteDeploymentFailure("pdh-collector-sample-interop-unavailable");
                return lastRead = CreateFallback(NativePdhResultCode.Unavailable, 0);
            }
            catch
            {
                CompleteDeploymentFailure("pdh-collector-sample-failed");
                throw;
            }

            if (sampleResult != NativePdhResultCode.Ok)
            {
                CompleteDeploymentFailure($"pdh-collector-sample-{sampleResult}".ToLowerInvariant());
                return lastRead = CreateFallback(sampleResult, header.NativeStatus);
            }

            if (!IsValidHeader(header)
                || header.EngineRowCount > NativeCoreAbi.MaximumFrameRows
                || header.MemoryRowCount > NativeCoreAbi.MaximumFrameRows)
            {
                CompleteDeploymentFailure("pdh-collector-invalid-frame-header");
                return lastRead = CreateFallback(NativePdhResultCode.AbiMismatch, header.NativeStatus);
            }

            var engineRows = new NativePdhGpuEngineRow[header.EngineRowCount];
            var memoryRows = new NativePdhGpuMemoryRow[header.MemoryRowCount];
            NativePdhResultCode copyResult;
            NativePdhSystemIo systemIo;
            try
            {
                copyResult = api.CopyFrame(handle, header.Sequence, engineRows, memoryRows, out systemIo);
            }
            catch (Exception exception) when (IsInteropUnavailable(exception))
            {
                permanentlyUnavailable = true;
                CompleteDeploymentFailure("pdh-collector-copy-interop-unavailable");
                return lastRead = CreateFallback(NativePdhResultCode.Unavailable, header.NativeStatus);
            }
            catch
            {
                CompleteDeploymentFailure("pdh-collector-copy-failed");
                throw;
            }

            if (copyResult != NativePdhResultCode.Ok
                || systemIo.StructSize != Marshal.SizeOf<NativePdhSystemIo>())
            {
                CompleteDeploymentFailure(copyResult == NativePdhResultCode.Ok
                    ? "pdh-collector-copy-system-io-shape"
                    : $"pdh-collector-copy-{copyResult}".ToLowerInvariant());
                return lastRead = CreateFallback(copyResult, header.NativeStatus);
            }

            var capturedAt = timeProvider.GetUtcNow();
            var frameFlags = NormalizeFrameFlags(header.Flags);
            lastGood = CreateAcceptedSnapshot(
                capturedAt,
                header.Sequence,
                header.TopologyGeneration,
                engineRows,
                memoryRows,
                systemIo,
                frameFlags,
                now);
            ApplyPlanConfiguration(plan);
            return lastRead = new NativePdhReadResult(
                lastGood,
                ResolveAggregateAvailability(lastGood),
                NativePdhResultCode.Ok,
                header.NativeStatus);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CompleteDeploymentFailure("pdh-collector-disposed-during-deployment");
            if (handle != IntPtr.Zero)
            {
                try
                {
                    api.Destroy(handle);
                }
                catch (Exception exception) when (IsInteropUnavailable(exception))
                {
                }
            }

            handle = IntPtr.Zero;
            createdPlan = null;
            appliedPlan = null;
        }
    }

    private bool EnsureInitialized(PdhCollectorRuntimePlan? plan)
    {
        if (handle != IntPtr.Zero)
        {
            return true;
        }

        if (initializationAttempted)
        {
            return false;
        }

        initializationAttempted = true;
        if (plan is not null)
        {
            activeDeploymentAttempt = runtime!.BeginInitialCreate(plan.HostPlan);
        }
        try
        {
            var expectedAbi = plan?.AbiVersion ?? NativeCoreAbi.Version;
            if (api.GetAbiVersion() != expectedAbi)
            {
                CompleteDeploymentFailure("pdh-collector-abi-mismatch");
                permanentlyUnavailable = true;
                return false;
            }

            var config = new NativePdhConfig
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = (uint)Marshal.SizeOf<NativePdhConfig>(),
                BaselineResetIntervalMilliseconds = checked((uint)(
                    plan?.Recreate.BaselineResetIntervalMilliseconds ?? 5_000))
            };
            var createResult = api.Create(config, out handle);
            if (createResult != NativePdhResultCode.Ok || handle == IntPtr.Zero)
            {
                handle = IntPtr.Zero;
                permanentlyUnavailable = createResult is NativePdhResultCode.AbiMismatch
                    or NativePdhResultCode.Unavailable;
                CompleteDeploymentFailure($"pdh-collector-create-{createResult}".ToLowerInvariant());
                return false;
            }

            createdPlan = plan;
            return true;
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            handle = IntPtr.Zero;
            permanentlyUnavailable = true;
            CompleteDeploymentFailure("pdh-collector-interop-unavailable");
            return false;
        }
        catch
        {
            handle = IntPtr.Zero;
            CompleteDeploymentFailure("pdh-collector-initialization-failed");
            throw;
        }
    }

    private PdhCollectorRuntimePlan? CaptureAppliedPlan()
    {
        if (runtime is null)
        {
            return null;
        }

        var desired = runtime.CaptureDesired();
        var lastGoodPlan = appliedPlan ?? createdPlan;
        if (lastGoodPlan is null)
        {
            return desired;
        }

        if (HasCompatibleNativeShape(lastGoodPlan, desired))
        {
            return desired;
        }
        if (failedRecreateRuntimePlanVersion == desired.RuntimePlanVersion)
        {
            return lastGoodPlan;
        }

        return TryRecreate(desired)
            ? desired
            : lastGoodPlan;
    }

    private static bool HasCompatibleNativeShape(
        PdhCollectorRuntimePlan current,
        PdhCollectorRuntimePlan desired)
        => string.Equals(
                current.HostPlan.BuildSha256,
                desired.HostPlan.BuildSha256,
                StringComparison.Ordinal)
            && string.Equals(
                current.HostPlan.DeploymentDigests.PdhCollector.RecreateSha256,
                desired.HostPlan.DeploymentDigests.PdhCollector.RecreateSha256,
                StringComparison.Ordinal);

    private bool TryRecreate(PdhCollectorRuntimePlan desired)
    {
        var attempt = runtime!.BeginHostRecreateAndHotPublish(desired.HostPlan);
        var replacement = IntPtr.Zero;
        var attemptApplied = false;
        try
        {
            if (api.GetAbiVersion() != desired.AbiVersion)
            {
                CompleteRecreateFailure(
                    attempt,
                    desired,
                    "pdh-collector-recreate-abi-mismatch");
                return false;
            }

            var config = new NativePdhConfig
            {
                AbiVersion = NativeCoreAbi.Version,
                StructSize = (uint)Marshal.SizeOf<NativePdhConfig>(),
                BaselineResetIntervalMilliseconds = checked((uint)
                    desired.Recreate.BaselineResetIntervalMilliseconds)
            };
            var result = api.Create(config, out replacement);
            if (result != NativePdhResultCode.Ok || replacement == IntPtr.Zero)
            {
                if (replacement != IntPtr.Zero)
                {
                    TryDestroyReplacement(replacement);
                }
                replacement = IntPtr.Zero;
                CompleteRecreateFailure(
                    attempt,
                    desired,
                    $"pdh-collector-recreate-create-{result}".ToLowerInvariant());
                return false;
            }

            if (runtime.CaptureDesired().RuntimePlanVersion != desired.RuntimePlanVersion)
            {
                TryDestroyReplacement(replacement);
                replacement = IntPtr.Zero;
                CompleteRecreateFailure(
                    attempt,
                    desired,
                    "pdh-collector-recreate-superseded");
                return false;
            }

            try
            {
                runtime.CompleteSucceeded(attempt);
                attemptApplied = true;
            }
            catch (InvalidOperationException) when (
                runtime.CaptureDesired().RuntimePlanVersion != desired.RuntimePlanVersion)
            {
                api.Destroy(replacement);
                replacement = IntPtr.Zero;
                return false;
            }

            var retired = handle;
            handle = replacement;
            replacement = IntPtr.Zero;
            createdPlan = desired;
            appliedPlan = desired;
            frameReuseWindow = TimeSpan.FromMilliseconds(
                desired.HotPublish.FrameReuseWindowMilliseconds);
            lastGoodLifetime = TimeSpan.FromMilliseconds(
                desired.HotPublish.LastGoodLifetimeMilliseconds);
            hasLastReadTimestamp = false;
            hasAdapterTopologyFingerprint = false;
            failedRecreateRuntimePlanVersion = -1;
            ClearAllDatasetState();

            if (retired != IntPtr.Zero)
            {
                try
                {
                    api.Destroy(retired);
                }
                catch (Exception exception) when (IsInteropUnavailable(exception))
                {
                }
            }

            return true;
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            if (replacement != IntPtr.Zero)
            {
                TryDestroyReplacement(replacement);
            }
            if (!attemptApplied)
            {
                CompleteRecreateFailure(
                    attempt,
                    desired,
                    "pdh-collector-recreate-interop-unavailable");
            }
            return false;
        }
        catch
        {
            if (replacement != IntPtr.Zero)
            {
                TryDestroyReplacement(replacement);
            }
            if (!attemptApplied)
            {
                CompleteRecreateFailure(
                    attempt,
                    desired,
                    "pdh-collector-recreate-failed");
            }
            throw;
        }
    }

    private void CompleteRecreateFailure(
        HostManagerDeploymentAttemptToken attempt,
        PdhCollectorRuntimePlan desired,
        string failureCode)
    {
        failedRecreateRuntimePlanVersion = desired.RuntimePlanVersion;
        try
        {
            runtime!.CompleteFailed(attempt, failureCode);
        }
        catch (InvalidOperationException) when (
            runtime!.CaptureDesired().RuntimePlanVersion != desired.RuntimePlanVersion)
        {
        }
    }

    private void TryDestroyReplacement(IntPtr replacement)
    {
        try
        {
            api.Destroy(replacement);
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
        }
    }

    private void ApplyPlanConfiguration(PdhCollectorRuntimePlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        try
        {
            frameReuseWindow = TimeSpan.FromMilliseconds(plan.HotPublish.FrameReuseWindowMilliseconds);
            lastGoodLifetime = TimeSpan.FromMilliseconds(plan.HotPublish.LastGoodLifetimeMilliseconds);
            appliedPlan = plan;
            if (activeDeploymentAttempt is { } attempt)
            {
                runtime!.CompleteSucceeded(attempt);
                activeDeploymentAttempt = null;
            }
        }
        catch
        {
            CompleteDeploymentFailure("pdh-collector-hot-publish-failed");
            throw;
        }
    }

    private void EnsureDeploymentAttempt(PdhCollectorRuntimePlan? plan)
    {
        if (plan is null
            || activeDeploymentAttempt is not null
            || runtime is null)
        {
            return;
        }
        if (appliedPlan is null)
        {
            activeDeploymentAttempt = runtime.BeginInitialCreate(plan.HostPlan);
            return;
        }
        if (!string.Equals(
                appliedPlan.HostPlan.DeploymentDigests.PdhCollector.HotPublishSha256,
                plan.HostPlan.DeploymentDigests.PdhCollector.HotPublishSha256,
                StringComparison.Ordinal))
        {
            activeDeploymentAttempt = runtime.BeginHotPublish(plan.HostPlan);
        }
    }

    private void CompleteDeploymentFailure(string failureCode)
    {
        if (activeDeploymentAttempt is not { } attempt)
        {
            return;
        }

        runtime!.CompleteFailed(attempt, failureCode);
        activeDeploymentAttempt = null;
    }

    private bool UpdateAdapterTopology(ulong? fingerprint)
    {
        if (fingerprint is null)
        {
            return false;
        }

        var changed = !hasAdapterTopologyFingerprint || adapterTopologyFingerprint != fingerprint.Value;
        adapterTopologyFingerprint = fingerprint.Value;
        hasAdapterTopologyFingerprint = true;
        return changed;
    }

    private NativePdhReadResult CreateFallback(NativePdhResultCode resultCode, int nativeStatus)
    {
        if (lastGood is not null)
        {
            var previous = lastGood;
            lastGood = CreateAcceptedSnapshot(
                previous.CapturedAt,
                previous.Sequence,
                previous.TopologyGeneration,
                [],
                [],
                default,
                NativePdhFrameFlags.None,
                timeProvider.GetTimestamp());
            if (ResolveAggregateAvailability(lastGood)
                == NativePdhProviderAvailability.LastGood)
            {
                return new NativePdhReadResult(
                    lastGood,
                    NativePdhProviderAvailability.LastGood,
                    resultCode,
                    nativeStatus);
            }
        }

        lastGood = null;

        return resultCode == NativePdhResultCode.NoData
            ? new NativePdhReadResult(null, NativePdhProviderAvailability.WarmingUp, resultCode, nativeStatus)
            : CreateUnavailable(resultCode, nativeStatus);
    }

    private NativePdhSnapshot CreateAcceptedSnapshot(
        DateTimeOffset capturedAt,
        ulong sequence,
        uint topologyGeneration,
        IReadOnlyList<NativePdhGpuEngineRow> engineRows,
        IReadOnlyList<NativePdhGpuMemoryRow> memoryRows,
        NativePdhSystemIo systemIo,
        NativePdhFrameFlags frameFlags,
        long now)
    {
        var previous = lastGood;
        var gpuEngineCurrent = frameFlags.HasFlag(
            NativePdhFrameFlags.GpuEngineComplete);
        var gpuMemoryCurrent = frameFlags.HasFlag(
            NativePdhFrameFlags.GpuMemoryComplete);
        /*
         * **一个域只要有一项读到了就算可用。**
         *
         * 先前这里要求域里的计数器**全都**有效（磁盘四项、网络四项），
         * 缺一项整个域判为不可用 —— 于是磁盘四项一起变暗，
         * 而实测 `% Disk Time`、`Disk Read Bytes/sec`、`Current Disk Queue Length`
         * 在系统层面都读得出来，只是有一项没到齐。
         *
         * 哪一项有值由它自己的位决定（读取端按位取），域的可用性只回答
         * "这条通道有没有东西"。一个次要计数器缺席不该把整组打掉 ——
         * 和"一条指标没数把整帧带走"是同一类错误。
         */
        var diskCurrent = frameFlags.HasFlag(
                NativePdhFrameFlags.DiskComplete)
            && HasAnyIoField(systemIo.ValidMask, DiskIoMask);
        var networkCurrent = frameFlags.HasFlag(
                NativePdhFrameFlags.NetworkComplete)
            && HasAnyIoField(systemIo.ValidMask, NetworkIoMask);

        var gpuEngineObservation = ResolveDatasetObservation(
            gpuEngineCurrent,
            capturedAt,
            sequence,
            previous?.GpuEngineObservation,
            ref hasGpuEngineLastGood,
            ref gpuEngineLastGoodTimestamp,
            now);
        var gpuMemoryObservation = ResolveDatasetObservation(
            gpuMemoryCurrent,
            capturedAt,
            sequence,
            previous?.GpuMemoryObservation,
            ref hasGpuMemoryLastGood,
            ref gpuMemoryLastGoodTimestamp,
            now);
        var diskObservation = ResolveDatasetObservation(
            diskCurrent,
            capturedAt,
            sequence,
            previous?.DiskObservation,
            ref hasDiskLastGood,
            ref diskLastGoodTimestamp,
            now);
        var networkObservation = ResolveDatasetObservation(
            networkCurrent,
            capturedAt,
            sequence,
            previous?.NetworkObservation,
            ref hasNetworkLastGood,
            ref networkLastGoodTimestamp,
            now);

        var acceptedEngineRows = gpuEngineCurrent
            ? engineRows
            : HasRetainedPayload(gpuEngineObservation)
                ? previous?.EngineRows ?? []
                : [];
        var acceptedMemoryRows = gpuMemoryCurrent
            ? memoryRows
            : HasRetainedPayload(gpuMemoryObservation)
                ? previous?.MemoryRows ?? []
                : [];
        var acceptedSystemIo = MergeSystemIo(
            systemIo,
            previous?.SystemIo,
            diskCurrent,
            networkCurrent,
            HasRetainedPayload(diskObservation),
            HasRetainedPayload(networkObservation));

        return new NativePdhSnapshot(
            capturedAt,
            sequence,
            topologyGeneration,
            acceptedEngineRows,
            acceptedMemoryRows,
            acceptedSystemIo,
            frameFlags,
            gpuEngineObservation,
            gpuMemoryObservation,
            diskObservation,
            networkObservation);
    }

    private NativePdhDatasetObservation ResolveDatasetObservation(
        bool isCurrent,
        DateTimeOffset capturedAt,
        ulong sequence,
        NativePdhDatasetObservation? previous,
        ref bool hasLastGood,
        ref long lastGoodTimestamp,
        long now)
    {
        if (isCurrent)
        {
            hasLastGood = true;
            lastGoodTimestamp = now;
            return NativePdhDatasetObservation.Current(
                capturedAt,
                sequence);
        }

        if (hasLastGood
            && previous is { ObservedAt: { } observedAt, Generation: > 0 }
            && timeProvider.GetElapsedTime(lastGoodTimestamp, now)
                <= lastGoodLifetime)
        {
            return NativePdhDatasetObservation.LastGood(
                observedAt,
                previous.Value.Generation);
        }

        hasLastGood = false;
        return NativePdhDatasetObservation.Unavailable;
    }

    private static NativePdhSystemIo MergeSystemIo(
        NativePdhSystemIo current,
        NativePdhSystemIo? previous,
        bool diskCurrent,
        bool networkCurrent,
        bool diskRetained,
        bool networkRetained)
    {
        var result = new NativePdhSystemIo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativePdhSystemIo>())
        };
        var disk = diskCurrent
            ? current
            : diskRetained && previous is { } previousDisk
                ? previousDisk
                : default;
        if (diskCurrent || diskRetained)
        {
            result.ValidMask |= disk.ValidMask & DiskIoMask;
            result.DiskActivePercent = disk.DiskActivePercent;
            result.DiskReadBytesPerSecond = disk.DiskReadBytesPerSecond;
            result.DiskWriteBytesPerSecond = disk.DiskWriteBytesPerSecond;
            result.DiskQueueLength = disk.DiskQueueLength;
        }

        var network = networkCurrent
            ? current
            : networkRetained && previous is { } previousNetwork
                ? previousNetwork
                : default;
        if (networkCurrent || networkRetained)
        {
            result.ValidMask |= network.ValidMask & NetworkIoMask;
            result.NetworkReceiveBytesPerSecond =
                network.NetworkReceiveBytesPerSecond;
            result.NetworkSendBytesPerSecond =
                network.NetworkSendBytesPerSecond;
            result.NetworkUtilizationPercent =
                network.NetworkUtilizationPercent;
            result.NetworkBandwidthBitsPerSecond =
                network.NetworkBandwidthBitsPerSecond;
        }
        return result;
    }

    private static NativePdhProviderAvailability ResolveAggregateAvailability(
        NativePdhSnapshot snapshot)
    {
        var observations = new[]
        {
            snapshot.GpuEngineObservation,
            snapshot.GpuMemoryObservation,
            snapshot.DiskObservation,
            snapshot.NetworkObservation
        };
        if (observations.Any(static observation =>
                observation.Availability
                    == NativePdhProviderAvailability.Available))
        {
            return NativePdhProviderAvailability.Available;
        }
        return observations.Any(static observation =>
                observation.Availability
                    == NativePdhProviderAvailability.LastGood)
            ? NativePdhProviderAvailability.LastGood
            : NativePdhProviderAvailability.Unavailable;
    }

    private static NativePdhFrameFlags NormalizeFrameFlags(uint value)
    {
        var flags = (NativePdhFrameFlags)value;
        if ((flags & NativePdhFrameFlags.AllDomainsComplete) == 0
            && !flags.HasFlag(NativePdhFrameFlags.Partial))
        {
            flags |= NativePdhFrameFlags.AllDomainsComplete;
        }
        return flags;
    }

    private void ClearGpuDatasetState()
    {
        hasGpuEngineLastGood = false;
        hasGpuMemoryLastGood = false;
    }

    private void ClearAllDatasetState()
    {
        lastGood = null;
        hasGpuEngineLastGood = false;
        hasGpuMemoryLastGood = false;
        hasDiskLastGood = false;
        hasNetworkLastGood = false;
    }

    private static bool HasRetainedPayload(
        NativePdhDatasetObservation observation)
        => observation.Availability
            is NativePdhProviderAvailability.Available
            or NativePdhProviderAvailability.LastGood;

    /// <summary>
    /// 这个域里有没有**任何一项**读到了。读到哪一项由各自的位说了算。
    /// </summary>
    private static bool HasAnyIoField(
        NativePdhIoValidMask actual,
        NativePdhIoValidMask domain)
        => (actual & domain) != 0;

    private const NativePdhIoValidMask DiskIoMask =
        NativePdhIoValidMask.DiskActive
        | NativePdhIoValidMask.DiskRead
        | NativePdhIoValidMask.DiskWrite
        | NativePdhIoValidMask.DiskQueue;

    private const NativePdhIoValidMask NetworkIoMask =
        NativePdhIoValidMask.NetworkReceive
        | NativePdhIoValidMask.NetworkSend
        | NativePdhIoValidMask.NetworkUtilization
        | NativePdhIoValidMask.NetworkBandwidth;

    private static NativePdhReadResult CreateUnavailable(NativePdhResultCode resultCode, int nativeStatus)
    {
        return new NativePdhReadResult(
            null,
            NativePdhProviderAvailability.Unavailable,
            resultCode,
            nativeStatus);
    }

    private static bool IsValidHeader(NativePdhFrameHeader header)
    {
        return header.AbiVersion == NativeCoreAbi.Version
            && header.StructSize == Marshal.SizeOf<NativePdhFrameHeader>();
    }

    private static bool IsInteropUnavailable(Exception exception)
    {
        return exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException;
    }
}

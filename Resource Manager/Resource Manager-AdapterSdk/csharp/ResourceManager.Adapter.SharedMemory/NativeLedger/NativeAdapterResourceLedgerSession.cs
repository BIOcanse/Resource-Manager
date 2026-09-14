using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.NativeLedger;

public sealed record NativeAdapterResourceLedgerConfiguration(
    ulong Generation,
    int Capacity,
    TimeSpan MaximumSnapshotAge,
    TimeSpan MaximumFutureClockSkew,
    TimeSpan ActivitySettlementInterval,
    byte ActivityIncrement,
    byte ActivityDecayNumerator,
    byte ActivityDecayDenominator);

public readonly record struct AdapterResourceLedgerReference(
    uint SlotIndex,
    uint SlotGeneration,
    ulong ResourceKey);

public sealed record NativeAdapterResourceLedgerSnapshot(
    AdapterResourceSnapshot Snapshot,
    ulong LedgerInstanceId,
    ulong LedgerGeneration,
    ulong ConfigurationGeneration,
    ulong Fingerprint,
    ulong OwnerApplicationKey,
    ulong OwnerProcessInstanceKey,
    int ResourceCapacity,
    IReadOnlyDictionary<ulong, AdapterResourceLedgerReference> References);

public sealed unsafe class NativeAdapterResourceLedgerSession : IDisposable
{
    private const byte ImportActivityFlag = 1 << 0;
    private const byte ImportDemandFlag = 1 << 1;
    private readonly object sync = new();
    private readonly NativeAdapterResourceInput[] resourceInputs;
    private readonly NativeAdapterResourceView[] resourceViews;
    private readonly uint[] touchedResourceIds;
    private readonly void* stateBuffer;
    private readonly ulong stateSize;
    private bool disposed;

    public NativeAdapterResourceLedgerSession(NativeAdapterResourceLedgerConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        EnsureNativeContract();
        ThrowIfFailed(
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_state_required_size(
                checked((uint)configuration.Capacity),
                out stateSize),
            "state-required-size");
        var alignment = NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_state_alignment();
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new InvalidOperationException($"Native private-resource ledger returned invalid alignment {alignment}.");
        }

        resourceInputs = new NativeAdapterResourceInput[configuration.Capacity];
        resourceViews = new NativeAdapterResourceView[configuration.Capacity];
        touchedResourceIds = new uint[configuration.Capacity];
        stateBuffer = NativeMemory.AlignedAlloc(checked((nuint)stateSize), alignment);
        if (stateBuffer is null)
        {
            throw new OutOfMemoryException($"Unable to allocate {stateSize} bytes for the native private-resource ledger.");
        }

        try
        {
            var native = ToNativeConfiguration(configuration);
            ThrowIfFailed(
                NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_state_initialize(
                    stateBuffer,
                    stateSize,
                    &native),
                "state-initialize");
        }
        catch
        {
            NativeMemory.AlignedFree(stateBuffer);
            throw;
        }
    }

    public int Capacity => resourceInputs.Length;

    public void ApplyConfiguration(NativeAdapterResourceLedgerConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        if (configuration.Capacity != Capacity)
        {
            throw new InvalidOperationException("A private-resource ledger capacity change requires session recreation.");
        }
        lock (sync)
        {
            ThrowIfDisposed();
            var native = ToNativeConfiguration(configuration);
            ThrowIfFailed(
                NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_apply_config(
                    stateBuffer,
                    stateSize,
                    &native),
                "apply-config");
        }
    }

    public NativeAdapterResourceLedgerSnapshot ImportSnapshot(
        AdapterResourceSnapshot snapshot,
        ulong ownerApplicationKey,
        ulong ownerProcessInstanceKey,
        bool importActivityState,
        bool importDemandState)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Sequence <= 0 || snapshot.Resources.Count > Capacity)
        {
            throw new ArgumentException("The snapshot sequence and resource count must fit the native session.", nameof(snapshot));
        }
        lock (sync)
        {
            ThrowIfDisposed();
            CopyResources(snapshot.Resources);
            var input = new NativeAdapterResourceSnapshotInput
            {
                AbiVersion = NativeAdapterResourceLedgerInterop.AbiVersion,
                StructSize = checked((uint)sizeof(NativeAdapterResourceSnapshotInput)),
                OwnerApplicationKey = ownerApplicationKey,
                OwnerProcessInstanceKey = ownerProcessInstanceKey,
                SourceSequence = checked((ulong)snapshot.Sequence),
                CapturedAtUnixMilliseconds = snapshot.CapturedAt.ToUnixTimeMilliseconds(),
                ObservedAtMonotonic = MonotonicNow(),
                NowUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                OwnerProcessId = checked((uint)snapshot.ProcessId),
                SchemaVersion = snapshot.SchemaVersion,
                SurfaceState = snapshot.SurfaceState,
                ImportFlags = ComposeImportFlags(importActivityState, importDemandState)
            };
            var summary = default(NativeAdapterResourceSnapshotSummary);
            fixed (NativeAdapterResourceInput* resources = resourceInputs)
            {
                ThrowIfFailed(
                    NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_import_snapshot(
                        stateBuffer,
                        stateSize,
                        &input,
                        resources,
                        checked((uint)snapshot.Resources.Count),
                        &summary),
                    "import-snapshot");
            }
            return ReadSnapshot(snapshot.ApplicationId, snapshot.ApplicationName, summary);
        }
    }

    public NativeAdapterResourceLedgerSnapshot TouchResources(
        string applicationId,
        string applicationName,
        IReadOnlyList<uint> resourceIds)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        lock (sync)
        {
            ThrowIfDisposed();
            CopyResourceIds(resourceIds);
            var summary = default(NativeAdapterResourceSnapshotSummary);
            fixed (uint* ids = touchedResourceIds)
            {
                ThrowIfFailed(
                    NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_touch_resources(
                        stateBuffer,
                        stateSize,
                        ids,
                        checked((uint)resourceIds.Count),
                        &summary),
                    "touch-resources");
            }
            return ReadSnapshot(applicationId, applicationName, summary);
        }
    }

    public NativeAdapterResourceLedgerSnapshot SettleActivity(
        string applicationId,
        string applicationName,
        ulong nowTimestamp)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var summary = default(NativeAdapterResourceSnapshotSummary);
            ThrowIfFailed(
                NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_settle_activity(
                    stateBuffer,
                    stateSize,
                    nowTimestamp,
                    &summary),
                "settle-activity");
            return ReadSnapshot(applicationId, applicationName, summary);
        }
    }

    public NativeAdapterResourceLedgerSnapshot ApplyActionFeedback(
        string applicationId,
        string applicationName,
        AdapterResourceLedgerReference reference,
        AdapterResourceActionResult result,
        AdapterResourceKind preferredGpuKind)
    {
        if (reference.ResourceKey != result.ResourceKey)
        {
            throw new ArgumentException("Action feedback does not match the resource reference.", nameof(result));
        }
        lock (sync)
        {
            ThrowIfDisposed();
            var native = new NativeAdapterResourceActionFeedback
            {
                AbiVersion = NativeAdapterResourceLedgerInterop.AbiVersion,
                StructSize = checked((uint)sizeof(NativeAdapterResourceActionFeedback)),
                Resource = new NativeAdapterResourceReference
                {
                    SlotIndex = reference.SlotIndex,
                    SlotGeneration = reference.SlotGeneration,
                    ResourceKey = reference.ResourceKey
                },
                Action = result.Action,
                Status = result.Status,
                CurrentTier = result.CurrentTier,
                PreferredGpuKind = preferredGpuKind,
                ResidentBytes = result.ResidentBytes,
                ReleasedBytes = result.ReleasedBytes
            };
            var summary = default(NativeAdapterResourceSnapshotSummary);
            ThrowIfFailed(
                NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_apply_action_feedback(
                    stateBuffer,
                    stateSize,
                    &native,
                    &summary),
                "apply-action-feedback");
            return ReadSnapshot(applicationId, applicationName, summary);
        }
    }

    public NativeAdapterResourceLedgerSnapshot ReadSnapshot(string applicationId, string applicationName)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            return ReadSnapshot(applicationId, applicationName, default);
        }
    }

    public static ulong MonotonicNow()
    {
        var value = Stopwatch.GetTimestamp();
        return value > 0 ? checked((ulong)value) : 1;
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    ~NativeAdapterResourceLedgerSession() => DisposeCore();

    private NativeAdapterResourceLedgerSnapshot ReadSnapshot(
        string applicationId,
        string applicationName,
        NativeAdapterResourceSnapshotSummary summary)
    {
        fixed (NativeAdapterResourceView* output = resourceViews)
        {
            ThrowIfFailed(
                NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_read_snapshot(
                    stateBuffer,
                    stateSize,
                    output,
                    checked((uint)resourceViews.Length),
                    &summary),
                "read-snapshot");
        }
        return Project(applicationId, applicationName, summary);
    }

    private NativeAdapterResourceLedgerSnapshot Project(
        string applicationId,
        string applicationName,
        NativeAdapterResourceSnapshotSummary summary)
    {
        if (summary.HasSnapshot != 1 || summary.ResourceCount > resourceViews.Length)
        {
            throw new InvalidDataException("The native private-resource ledger returned an invalid summary.");
        }
        var resources = new TieredResourceEntry[summary.ResourceCount];
        var references = new Dictionary<ulong, AdapterResourceLedgerReference>(resources.Length);
        for (var index = 0; index < resources.Length; index++)
        {
            var source = resourceViews[index];
            if (source.LedgerGeneration != summary.LedgerGeneration || source.Resource.ResourceKey != source.Value.ResourceKey)
            {
                throw new InvalidDataException("The native private-resource ledger returned inconsistent resource identity.");
            }
            resources[index] = ToManaged(source.Value);
            references.Add(source.Value.ResourceKey, new AdapterResourceLedgerReference(
                source.Resource.SlotIndex,
                source.Resource.SlotGeneration,
                source.Resource.ResourceKey));
        }
        var snapshot = new AdapterResourceSnapshot(
            AdapterResourceProtocol.SnapshotSchemaVersion,
            applicationId,
            applicationName,
            checked((int)summary.OwnerProcessId),
            summary.SurfaceState,
            checked((long)summary.SourceSequence),
            DateTimeOffset.FromUnixTimeMilliseconds(summary.CapturedAtUnixMilliseconds),
            resources);
        return new NativeAdapterResourceLedgerSnapshot(
            snapshot,
            summary.LedgerInstanceId,
            summary.LedgerGeneration,
            summary.ConfigurationGeneration,
            summary.Fingerprint,
            summary.OwnerApplicationKey,
            summary.OwnerProcessInstanceKey,
            Capacity,
            references);
    }

    private void CopyResources(IReadOnlyList<TieredResourceEntry> resources)
    {
        for (var index = 0; index < resources.Count; index++)
        {
            var source = resources[index];
            resourceInputs[index] = new NativeAdapterResourceInput
            {
                ResourceKey = source.ResourceKey,
                SizeBytes = source.SizeBytes,
                ResourceId = source.ResourceId,
                Tier = source.Tier,
                ResourceKind = source.ResourceKind,
                RecoveryKind = source.RecoveryKind,
                Granularity = source.Granularity,
                InapplicableActions = source.InapplicableActions,
                ActionRoute = source.ActionRoute,
                ActivityScore = source.ActivityScore,
                DemandMask = source.FrontendDemandMask
            };
        }
    }

    private void CopyResourceIds(IReadOnlyList<uint> resourceIds)
    {
        if (resourceIds.Count > touchedResourceIds.Length)
        {
            throw new InvalidOperationException("The touched resource-id count exceeds ledger capacity.");
        }
        for (var index = 0; index < resourceIds.Count; index++) touchedResourceIds[index] = resourceIds[index];
    }

    private static TieredResourceEntry ToManaged(NativeAdapterResourceInput source)
        => new(
            source.ResourceKey,
            source.ResourceId,
            source.SizeBytes,
            source.Tier,
            source.ResourceKind,
            source.RecoveryKind,
            source.Granularity,
            source.InapplicableActions,
            source.ActionRoute,
            source.ActivityScore,
            source.DemandMask);

    private static byte ComposeImportFlags(bool activity, bool demand)
        => (byte)((activity ? ImportActivityFlag : 0) |
            (demand ? ImportDemandFlag : 0));

    private static NativeAdapterResourceLedgerConfig ToNativeConfiguration(
        NativeAdapterResourceLedgerConfiguration source)
        => new()
        {
            AbiVersion = NativeAdapterResourceLedgerInterop.AbiVersion,
            StructSize = checked((uint)sizeof(NativeAdapterResourceLedgerConfig)),
            ConfigurationGeneration = source.Generation,
            Capacity = checked((uint)source.Capacity),
            MaximumSnapshotAgeMilliseconds = ToUInt32Milliseconds(source.MaximumSnapshotAge),
            MaximumFutureSkewMilliseconds = ToUInt32Milliseconds(source.MaximumFutureClockSkew, allowZero: true),
            ActivitySettlementInterval = ToMonotonicDuration(source.ActivitySettlementInterval),
            ActivityIncrement = source.ActivityIncrement,
            ActivityDecayNumerator = source.ActivityDecayNumerator,
            ActivityDecayDenominator = source.ActivityDecayDenominator
        };

    private static void ValidateConfiguration(NativeAdapterResourceLedgerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Generation == 0 || configuration.Capacity <= 0 ||
            configuration.MaximumSnapshotAge <= TimeSpan.Zero ||
            configuration.MaximumFutureClockSkew < TimeSpan.Zero ||
            configuration.ActivitySettlementInterval <= TimeSpan.Zero ||
            configuration.ActivityIncrement == 0 || configuration.ActivityDecayDenominator == 0 ||
            configuration.ActivityDecayNumerator > configuration.ActivityDecayDenominator)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private static uint ToUInt32Milliseconds(TimeSpan value, bool allowZero = false)
    {
        var milliseconds = value.TotalMilliseconds;
        if (!double.IsFinite(milliseconds) || milliseconds < (allowZero ? 0 : 1) || milliseconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return checked((uint)Math.Ceiling(milliseconds));
    }

    private static ulong ToMonotonicDuration(TimeSpan value)
    {
        var ticks = value.TotalSeconds * Stopwatch.Frequency;
        if (!double.IsFinite(ticks) || ticks < 1 || ticks > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return checked((ulong)Math.Ceiling(ticks));
    }

    private static void EnsureNativeContract()
    {
        if (NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_abi_version() != NativeAdapterResourceLedgerInterop.AbiVersion ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_config_size() != sizeof(NativeAdapterResourceLedgerConfig) ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_snapshot_input_size() != sizeof(NativeAdapterResourceSnapshotInput) ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_resource_input_size() != sizeof(NativeAdapterResourceInput) ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_resource_view_size() != sizeof(NativeAdapterResourceView) ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_action_feedback_size() != sizeof(NativeAdapterResourceActionFeedback) ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_summary_size() != sizeof(NativeAdapterResourceSnapshotSummary) ||
            NativeAdapterResourceLedgerInterop.rm_private_resource_ledger_layout_fingerprint() != ManagedLayoutFingerprint())
        {
            throw new InvalidOperationException("Private-resource ledger ABI layout mismatch.");
        }
    }

    private static ulong ManagedLayoutFingerprint()
    {
        ReadOnlySpan<ulong> values =
        [
            checked((ulong)sizeof(NativeAdapterResourceLedgerConfig)),
            OffsetOf<NativeAdapterResourceLedgerConfig>(nameof(NativeAdapterResourceLedgerConfig.ConfigurationGeneration)),
            OffsetOf<NativeAdapterResourceLedgerConfig>(nameof(NativeAdapterResourceLedgerConfig.Capacity)),
            checked((ulong)sizeof(NativeAdapterResourceReference)),
            OffsetOf<NativeAdapterResourceReference>(nameof(NativeAdapterResourceReference.SlotIndex)),
            OffsetOf<NativeAdapterResourceReference>(nameof(NativeAdapterResourceReference.SlotGeneration)),
            OffsetOf<NativeAdapterResourceReference>(nameof(NativeAdapterResourceReference.ResourceKey)),
            checked((ulong)sizeof(NativeAdapterResourceView)),
            OffsetOf<NativeAdapterResourceView>(nameof(NativeAdapterResourceView.Resource)),
            OffsetOf<NativeAdapterResourceView>(nameof(NativeAdapterResourceView.LedgerGeneration)),
            OffsetOf<NativeAdapterResourceView>(nameof(NativeAdapterResourceView.Value)),
            checked((ulong)sizeof(NativeAdapterResourceSnapshotSummary)),
            OffsetOf<NativeAdapterResourceSnapshotSummary>(nameof(NativeAdapterResourceSnapshotSummary.LedgerGeneration)),
            OffsetOf<NativeAdapterResourceSnapshotSummary>(nameof(NativeAdapterResourceSnapshotSummary.ConfigurationGeneration)),
            OffsetOf<NativeAdapterResourceSnapshotSummary>(nameof(NativeAdapterResourceSnapshotSummary.LedgerInstanceId)),
            OffsetOf<NativeAdapterResourceSnapshotSummary>(nameof(NativeAdapterResourceSnapshotSummary.OwnerApplicationKey)),
            OffsetOf<NativeAdapterResourceSnapshotSummary>(nameof(NativeAdapterResourceSnapshotSummary.ResourceCount)),
            OffsetOf<NativeAdapterResourceSnapshotSummary>(nameof(NativeAdapterResourceSnapshotSummary.SurfaceState))
        ];

        var hash = 14695981039346656037UL;
        foreach (var value in values)
        {
            hash = unchecked((hash ^ value) * 1099511628211UL);
        }
        return hash;
    }

    private static ulong OffsetOf<T>(string fieldName)
        where T : unmanaged
        => checked((ulong)Marshal.OffsetOf<T>(fieldName).ToInt64());

    private static void ThrowIfFailed(NativeAdapterResourceLedgerResultCode result, string operation)
    {
        if (result != NativeAdapterResourceLedgerResultCode.Ok)
        {
            throw new NativeAdapterResourceLedgerException(operation, (int)result);
        }
    }

    private void DisposeCore()
    {
        lock (sync)
        {
            if (disposed) return;
            NativeMemory.AlignedFree(stateBuffer);
            disposed = true;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

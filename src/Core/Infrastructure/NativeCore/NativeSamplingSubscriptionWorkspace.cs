using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Initialize keeps only the seven role configurations; no role owns a Native
// session yet. A role's session is built the first time a lease reads it, and
// is registered as sampling-role{N}-native with its stored and live rows. When
// the role's last lease returns and its session tracks no source and no item,
// the registration and the session are closed, so a role without subscriptions
// holds no memory. The next use builds a fresh session; the tracker replays its
// in-use sources onto it through its existing incarnation path. With a scratch
// factory, each operation borrows only the item rows it reads (sampling-session).
internal sealed class NativeSamplingSubscriptionWorkspace : IDisposable
{
    private readonly RoleSlot[] slots = Enumerable.Range(1, CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
        .Select(static roleId => new RoleSlot(roleId)).ToArray();
    private NativeSamplingSubscriptionConfiguration[]? configurations;
    private Func<ulong, IComputeScratch>? createScratch;
    private IWorkspaceCapacityRegistry? registry;
    private bool initializationStarted;
    private volatile bool initialized;
    private volatile bool disposed;

    public void Initialize(CompiledHostManagerSamplingSubscriptionPlan plan,
        Func<ulong, IComputeScratch>? createScratch = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initializationStarted)
            throw new InvalidOperationException("Sampling subscription workspace initialization was already attempted.");
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Sampling subscription plan is not published.");
        }

        initializationStarted = true;
        var built = new NativeSamplingSubscriptionConfiguration[slots.Length];
        for (var index = 0; index < slots.Length; index++)
        {
            built[index] = CreateConfiguration(
                plan.Build.AbiVersion,
                plan.Recreate.Roles[index],
                plan.HotPublish.Roles[index],
                plan.ConfigurationGeneration);
        }
        configurations = built;
        this.createScratch = createScratch;
        registry = createScratch?.Target as IWorkspaceCapacityRegistry;
        initialized = true;
    }

    /// <summary>Counts one lease of the role. Its session is built only when the lease reads it.</summary>
    internal RoleLease AcquireRole(int roleId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!initialized)
            throw new InvalidOperationException("Sampling subscription workspace is not initialized.");
        if (roleId is < 1 or > CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(roleId));
        }
        var slot = slots[roleId - 1];
        var lease = new RoleLease(this, slot);
        lock (slot.Sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            slot.Uses = checked(slot.Uses + 1);
        }
        return lease;
    }

    /// <summary>Whether the role currently owns a Native session; it changes when a lease builds or releases one.</summary>
    [global::ResourceManager.ReadWrite.RuntimeRead(global::ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "sampling role lease")]
    internal bool HasSession(int roleId)
    {
        if (roleId is < 1 or > CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
            throw new ArgumentOutOfRangeException(nameof(roleId));
        var slot = slots[roleId - 1];
        lock (slot.Sync) return slot.Session is not null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var slot in slots)
        {
            lock (slot.Sync) Close(slot);
        }
    }

    private NativeSamplingSubscriptionSession SessionFor(RoleSlot slot)
    {
        lock (slot.Sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (slot.Session is not null) return slot.Session;
            var configuration = configurations![slot.RoleId - 1];
            var factory = createScratch;
            var session = factory is null
                ? new NativeSamplingSubscriptionSession(in configuration)
                : new NativeSamplingSubscriptionSession(in configuration,
                    rows => SamplingSessionScratch.Create(factory, rows));
            try
            {
                slot.Registration = registry?.RegisterNativeWorkspace(
                    $"sampling-role{slot.RoleId}-native", () => Describe(session));
            }
            catch (Exception failure)
            {
                try { session.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
                throw;
            }
            slot.Session = session;
            return session;
        }
    }

    private void Release(RoleSlot slot)
    {
        lock (slot.Sync)
        {
            if (slot.Uses <= 0) throw new InvalidOperationException("Sampling role lease was already returned.");
            slot.Uses--;
            if (slot.Uses != 0 || slot.Session is null || disposed) return;
            var stored = slot.Session.WorkspaceCapacity;
            if (stored.ActiveSourceCount != 0 || stored.KnownItemCount != 0) return; // Still subscribed.
            Close(slot);
        }
    }

    private static void Close(RoleSlot slot)
    {
        // Unregister first: a diagnostic read never reaches a closed session.
        slot.Registration?.Dispose();
        slot.Registration = null;
        slot.Session?.Dispose();
        slot.Session = null;
    }

    private static NativeWorkspaceCapacityDescription Describe(NativeSamplingSubscriptionSession session)
    {
        var stored = session.WorkspaceCapacity;
        return new(
            $"sources={stored.SourceRows}/live={stored.ActiveSourceCount}, " +
            $"items={stored.ItemRows}/live={stored.KnownItemCount}, " +
            $"memberships={stored.MembershipRows}, triggers={stored.IssuedTriggerRows}",
            stored.ResidentBytes);
    }

    internal static NativeSamplingSubscriptionConfiguration CreateConfiguration(
        uint abiVersion,
        CompiledHostManagerSamplingSubscriptionRoleCapacityPlan capacity,
        CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hot,
        ulong generation)
    {
        if (capacity.RoleId != hot.RoleId || generation == 0)
        {
            throw new InvalidOperationException("Sampling subscription role configuration identity is invalid.");
        }

        return new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = abiVersion,
            StructSize = checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
            Generation = generation,
            MaximumSourceCount = checked((uint)capacity.MaximumSourceCount),
            MaximumItemCount = checked((uint)capacity.MaximumItemCount),
            MaximumMembershipCount = checked((uint)capacity.MaximumMembershipCount),
            MaximumDueItemCount = checked((uint)capacity.MaximumDueItemCount),
            MaximumSourceViewCount = checked((uint)capacity.MaximumSourceViewCount),
            MaximumExpiredSourceCount = checked((uint)capacity.MaximumExpiredSourceCount),
            DefaultIntervalMilliseconds = checked((ulong)hot.DefaultIntervalMilliseconds),
            MinimumIntervalMilliseconds = checked((ulong)hot.MinimumIntervalMilliseconds),
            ActiveTtlMilliseconds = checked((ulong)hot.ActiveTtlMilliseconds),
            MaximumFutureSkewMilliseconds = checked((ulong)hot.MaximumFutureSkewMilliseconds),
            Flags = 0,
            ReservedU64_0 = 0,
            ReservedU64_1 = 0,
            ReservedU64_2 = 0,
            ReservedU32_0 = 0,
            ReservedU32_1 = 0
        };
    }

    /// <summary>One lease of a role; the first read of Session builds the role's session.</summary>
    internal sealed class RoleLease : IDisposable
    {
        private readonly RoleSlot slot;
        private NativeSamplingSubscriptionWorkspace? owner;

        internal RoleLease(NativeSamplingSubscriptionWorkspace owner, RoleSlot slot)
        {
            this.owner = owner;
            this.slot = slot;
        }

        internal int RoleId => slot.RoleId;

        internal NativeSamplingSubscriptionSession Session
            => (Volatile.Read(ref owner) ?? throw new ObjectDisposedException(nameof(RoleLease))).SessionFor(slot);

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(slot);
    }

    internal sealed class RoleSlot(int roleId)
    {
        internal readonly object Sync = new();
        internal readonly int RoleId = roleId;
        internal int Uses;
        internal NativeSamplingSubscriptionSession? Session;
        internal IDisposable? Registration;
    }
}

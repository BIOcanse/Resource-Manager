using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

// Created only by explicit Start. The actual native session stays alive through
// the tracker's pending-plan lease and its final Clear, exactly as the original owner.
internal sealed class StandaloneSamplingSubscriptionSessionOwner : ISamplingSubscriptionSessionOwner<SubscriptionRunPermit>, IDisposable
{
    private readonly object gate = new();
    private readonly NativeSamplingSubscriptionSession session;
    private readonly ISubscriptionRoleSettings settings;
    private readonly ulong incarnation;
    private int leases;
    private bool disposed;
    internal SubscriptionRunPermit Permit { get; }

    internal StandaloneSamplingSubscriptionSessionOwner(ISubscriptionRoleSettings settings,
        in NativeSamplingSubscriptionConfiguration configuration, ulong incarnation)
    {
        this.settings = settings;
        this.incarnation = incarnation;
        session = new(in configuration);
        Permit = new(incarnation, configuration.Generation);
    }

    public ISamplingSubscriptionSessionLease<SubscriptionRunPermit> Acquire(int roleId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (roleId != settings.RoleId) throw new ArgumentOutOfRangeException(nameof(roleId));
            leases++;
            return new Lease(this);
        }
    }
    public ISamplingSubscriptionSessionLease<SubscriptionRunPermit>? TryAcquireForCleanup(int roleId) => Acquire(roleId);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (leases != 0) throw new InvalidOperationException("The native tracker still owns a pending plan lease.");
            Permit.Revoke();
            disposed = true;
            session.Dispose();
        }
    }
    private sealed class Lease : ISamplingSubscriptionSessionLease<SubscriptionRunPermit>
    {
        private readonly StandaloneSamplingSubscriptionSessionOwner owner;
        private StandaloneSamplingSubscriptionSessionOwner? holder;
        internal Lease(StandaloneSamplingSubscriptionSessionOwner owner)
        { this.owner = owner; holder = owner; }
        public NativeSamplingSubscriptionSession Session => owner.session;
        public ISubscriptionRoleSettings HotPublish => owner.settings;
        public ulong WorkspaceIncarnation => owner.incarnation;
        public SubscriptionRunPermit OwnerToken => owner.Permit;
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref holder, null);
            if (previous is not null) lock (previous.gate) previous.leases--;
        }
    }
}

internal sealed class SubscriptionRunPermit(ulong incarnation, ulong generation) : IMetricPublicationPermit
{
    private readonly object publicationGate = new();
    private bool active = true;
    public ulong WorkspaceIncarnation => incarnation;
    public ulong ConfigurationGeneration => generation;
    public bool IsActive { get { lock (publicationGate) return active; } }
    public bool TryPublish(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (publicationGate)
        {
            if (!active) return false;
            commit();
            return true;
        }
    }
    internal void Revoke() { lock (publicationGate) active = false; }
}

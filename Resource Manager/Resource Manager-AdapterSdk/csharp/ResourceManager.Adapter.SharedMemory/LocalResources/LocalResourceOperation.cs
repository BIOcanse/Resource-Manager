namespace ResourceManager.Adapter.LocalResources;

public sealed class LocalResourceOperation : IDisposable, IAsyncDisposable
{
    private readonly LocalResourceIntentExecutor _executor;
    private int _completionState;

    internal LocalResourceOperation(
        NativeLocalResourceManagerSession session,
        NativeLocalResourceOperationToken nativeToken)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        NativeToken = nativeToken;
        Token = NativeLocalResourceManagerSession.ToManaged(nativeToken);
        _executor = new(this);
    }

    public LocalResourceOperationToken Token { get; }
    public LocalResourceOperationKind Kind => Token.Kind;
    public ulong OperationId => Token.OperationId;
    public ulong OperationGeneration => Token.OperationGeneration;
    internal NativeLocalResourceManagerSession Session { get; }
    internal NativeLocalResourceOperationToken NativeToken { get; }
    internal bool IsActive => Volatile.Read(ref _completionState) == 0;

    public LocalResourcePlan PlanCapacity(LocalResourceCapacityStrategy strategy)
    {
        RequireKind(LocalResourceOperationKind.Cleanup);
        return Session.PlanCapacityScoped(this, strategy).Plan;
    }

    public LocalResourcePlan PlanMode(LocalResourceModeRequest request)
    {
        RequireKind(LocalResourceOperationKind.Cleanup);
        return Session.PlanModeScoped(this, request);
    }

    public LocalResourcePlan MergePlans(LocalResourcePlan capacity, LocalResourcePlan mode)
    {
        RequireKind(LocalResourceOperationKind.Cleanup);
        return Session.MergePlansScoped(this, capacity, mode);
    }

    public ValueTask<LocalResourceExecutionResult> ExecuteAsync(
        LocalResourceIntent intent,
        CancellationToken cancellationToken = default)
    {
        RequireActive();
        return _executor.ExecuteAsync(intent, cancellationToken);
    }

    public LocalResourcePartitionAdmissionPlan PlanPartitionAdmission(
        LocalResourceTableHandle table,
        int capacity,
        LocalResourcePartitionHandle? parent = null)
    {
        RequireKind(LocalResourceOperationKind.PartitionAdmission);
        return Session.PlanPartitionAdmissionScoped(this, table, capacity, parent);
    }

    public LocalResourcePartitionHandle CommitPartitionAdmission(
        LocalResourcePartitionAdmissionPlan plan)
    {
        RequireKind(LocalResourceOperationKind.PartitionAdmission);
        return Session.CommitPartitionAdmissionScoped(this, plan);
    }

    public LocalResourcePartitionClosePlan PlanPartitionClose(
        LocalResourcePartitionHandle partition)
    {
        RequireKind(LocalResourceOperationKind.PartitionAdmission);
        return Session.PlanPartitionCloseScoped(this, partition);
    }

    public void CommitPartitionClose(LocalResourcePartitionClosePlan plan)
    {
        RequireKind(LocalResourceOperationKind.PartitionAdmission);
        Session.CommitPartitionCloseScoped(this, plan);
    }

    public LocalResourceTableHandle RegisterTable(LocalResourceTableDefinition definition)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        return Session.RegisterTableScoped(this, definition);
    }

    public void UnregisterTable(LocalResourceTableHandle table)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.UnregisterTableScoped(this, table);
    }

    public LocalResourceCapabilityHandle RegisterCapability(
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        return Session.Capabilities.RegisterScoped(this, table, definition, handler);
    }

    public LocalResourceCapabilityHandle ReplaceCapability(
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        return Session.Capabilities.ReplaceScoped(this, handle, definition, handler);
    }

    public void UnregisterCapability(LocalResourceCapabilityHandle handle)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.Capabilities.UnregisterScoped(this, handle);
    }

    public LocalResourceHandle RegisterResource(
        LocalResourceTableHandle table,
        LocalResourceDefinition definition)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        return Session.RegisterResourceScoped(this, table, definition);
    }

    public LocalResourceHandle RegisterResource(
        LocalResourcePartitionHandle partition,
        int localOrdinal,
        LocalResourceDefinition definition)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        return Session.RegisterPartitionResourceScoped(this, partition, localOrdinal, definition);
    }

    public void UpdateResource(LocalResourceHandle handle, LocalResourceDefinition definition)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.UpdateResourceScoped(this, handle, definition);
    }

    public void UnregisterResource(LocalResourceHandle handle)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.UnregisterResourceScoped(this, handle);
    }

    public void Touch(LocalResourceHandle handle, uint weight = 1)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.TouchScoped(this, handle, weight);
    }

    public void AdvanceActivityEpoch(ulong count = 1)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.AdvanceActivityEpochScoped(this, count);
    }

    public LocalResourceUseLease BeginUse(LocalResourceHandle handle)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        return Session.BeginUseScoped(this, handle);
    }

    public void EndUse(LocalResourceUseLease lease)
    {
        RequireKind(LocalResourceOperationKind.Maintenance);
        Session.EndUseScoped(this, lease);
    }

    public void Complete() => Dispose();

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0) return;
        try
        {
            Session.CompleteStandaloneOperation(this);
            Volatile.Write(ref _completionState, 2);
        }
        catch
        {
            Volatile.Write(ref _completionState, 0);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void RequireActive()
    {
        if (!IsActive)
        {
            throw new ObjectDisposedException(
                nameof(LocalResourceOperation),
                "The local resource operation has already completed.");
        }
    }

    private void RequireKind(LocalResourceOperationKind expected)
    {
        RequireActive();
        if (Kind != expected)
        {
            throw new InvalidOperationException(
                $"Operation kind {Kind} cannot perform an action that requires {expected}.");
        }
    }
}

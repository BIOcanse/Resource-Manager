namespace ResourceManager.Subscription;

public enum SubscriptionFieldGroup
{
    GraphicsApiObservation,
    ProcessDedicatedGpuMemory,
    VramResidual,
    DiskAttribution,
    NetworkAttribution
}

/// <summary>Subscription-owned current activity. Reads never acquire a sampling workspace.</summary>
public sealed class SubscriptionActivity
{
    private readonly object gate = new();
    private Dictionary<int, Func<bool>> roles = [];
    private Dictionary<SubscriptionFieldGroup, Func<bool>> fields = [];
    public event Action<int>? RoleChanged;
    public event Action<SubscriptionFieldGroup>? FieldChanged;

    public bool HasInUseSubscriptions(int roleId)
    {
        Volatile.Read(ref roles).TryGetValue(roleId, out var read);
        return read?.Invoke() ?? false;
    }

    public bool HasInUseSubscriptions(SubscriptionFieldGroup group)
    {
        Volatile.Read(ref fields).TryGetValue(group, out var read);
        return read?.Invoke() ?? false;
    }

    public void BindRole(int roleId, Func<bool> read, Action<Action> subscribe)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (gate) Volatile.Write(ref roles, new Dictionary<int, Func<bool>>(roles) { [roleId] = read });
        var last = read();
        subscribe(() =>
        {
            var next = read();
            lock (gate)
            {
                if (roles.GetValueOrDefault(roleId) != read || last == next) return;
                last = next;
            }
            RoleChanged?.Invoke(roleId);
        });
        if (last) RoleChanged?.Invoke(roleId);
    }

    public void BindField(SubscriptionFieldGroup group, Func<bool> read, Action<Action> subscribe)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (gate) Volatile.Write(ref fields, new Dictionary<SubscriptionFieldGroup, Func<bool>>(fields) { [group] = read });
        var last = read();
        subscribe(() =>
        {
            var next = read();
            lock (gate)
            {
                if (fields.GetValueOrDefault(group) != read || last == next) return;
                last = next;
            }
            FieldChanged?.Invoke(group);
        });
        if (last) FieldChanged?.Invoke(group);
    }
}

/// <summary>Allows tracking while the controlled sampling owner is stopped.</summary>
public interface ISamplingSubscriptionSettingsSource
{
    /// <summary>Returns the immutable published policy, or null before publication, without locking, allocating or creating a workspace.</summary>
    ISubscriptionRoleSettings? GetRoleSettings(int roleId);
}

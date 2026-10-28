namespace ResourceManager.Shared.BusinessBlocks.State;

public enum NoValueReason { NotPublished, OwnerStopped, ReadFailed }

/// <summary>A current immutable value or an explicit absence; it never samples or waits.</summary>
public readonly struct StateRead<T>
{
    private readonly T? value;
    private readonly NoValueReason reason;
    public bool HasValue { get; }
    public T Value => HasValue ? value! : throw new InvalidOperationException($"State has no value: {NoValueReason}.");
    public NoValueReason? NoValueReason => HasValue ? null : reason;
    public T GetValueOrDefault(T absentValue) => HasValue ? value! : absentValue;
    private StateRead(T value) { this.value = value; HasValue = true; reason = default; }
    private StateRead(NoValueReason reason) { value = default; HasValue = false; this.reason = reason; }
    public static StateRead<T> FromValue(T value) => new(value);
    public static StateRead<T> NoValue(NoValueReason reason) => new(reason);
}

public readonly record struct StateItemKey(int ItemUid, int? BlockUid = null)
{
    public static implicit operator StateItemKey(int uid) => new(uid);
    public override string ToString() => BlockUid is { } block ? $"{ItemUid}/{block}" : ItemUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum StateItemStorage { Own, Reference }
public enum StateItemTrigger { Event, Poll }
public enum StateItemOwnerKind { Component, Block }
public sealed record StateItemOwner(StateItemOwnerKind Kind, int? BlockUid = null, string? ComponentLabel = null);
public sealed record StateItemDeclaration(int Uid, StateItemStorage Storage, Type ValueType,
    StateItemOwner Owner, bool PerBlock, StateItemTrigger Trigger, string? PollInterval = null);

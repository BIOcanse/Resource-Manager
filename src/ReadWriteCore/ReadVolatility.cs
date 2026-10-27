namespace ResourceManager.ReadWrite;

public enum ReadVolatilityKind { Fixed, EventDriven, Sampled }

/// <summary>How the source value changes within its declared owner identity.
/// A fixed process/device/record field is fixed only for that exact owner, not for a reused PID or slot.</summary>
public sealed record ReadVolatility
{
    public ReadVolatilityKind Kind { get; }
    public string? Event { get; }

    public ReadVolatility(ReadVolatilityKind kind, string? @event = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == ReadVolatilityKind.EventDriven)
            ArgumentException.ThrowIfNullOrWhiteSpace(@event);
        else if (@event is not null)
            throw new ArgumentException("Only an event-driven read declares a change event.", nameof(@event));
        Kind = kind;
        Event = @event;
    }

    public static ReadVolatility Fixed { get; } = new(ReadVolatilityKind.Fixed);
    public static ReadVolatility Sampled { get; } = new(ReadVolatilityKind.Sampled);
    public static ReadVolatility EventDriven(string changeEvent) => new(ReadVolatilityKind.EventDriven, changeEvent);
}

/// <summary>Classifies a raw read at its owning declaration; transports serving mixed queries are classified at each caller.</summary>
[AttributeUsage(AttributeTargets.Method
    | AttributeTargets.Constructor | AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class RuntimeReadAttribute : Attribute
{
    public ReadVolatilityKind Kind { get; }
    public string? Event { get; }

    public RuntimeReadAttribute(ReadVolatilityKind kind, string? changeEvent = null)
    {
        var volatility = new ReadVolatility(kind, changeEvent);
        Kind = volatility.Kind;
        Event = volatility.Event;
    }
}

/// <summary>A transport serves reads with different volatility; each owning caller declares its source semantics.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RuntimeReadTransportAttribute : Attribute { }

/// <summary>The original owner's read-once boundary. Raw reads inside this method still require a source declaration.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RuntimeReadCacheAttribute : Attribute { }

/// <summary>Binds a collector to the volatility on its existing source catalogue declaration.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RuntimeReadSourceAttribute : Attribute
{
    public Type SourceOwner { get; }
    public string SourceField { get; }

    public RuntimeReadSourceAttribute(Type sourceOwner, string sourceField)
    {
        ArgumentNullException.ThrowIfNull(sourceOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);
        SourceOwner = sourceOwner;
        SourceField = sourceField;
    }
}

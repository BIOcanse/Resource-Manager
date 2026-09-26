using System.Collections.Generic;
using System.Threading;

using System.Text.Json.Serialization;

namespace ResourceManager.Adapter;

public enum AdapterResourceActionStatus : byte
{
    Completed = 0,
    ResourceNotFound = 1,
    ActionNotSupported = 2,
    InvalidRequest = 3,
    ResourceBusy = 4,
    Failed = 5
}

public readonly struct AdapterResourceActionRequest
{
    [JsonConstructor]
    public AdapterResourceActionRequest(
        ulong requestId,
        ulong resourceKey,
        AdapterResourceActionMask action,
        byte flags = 0)
    {
        RequestId = requestId;
        ResourceKey = resourceKey;
        Action = action;
        Flags = flags;
    }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong RequestId { get; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong ResourceKey { get; }
    public AdapterResourceActionMask Action { get; }
    public byte Flags { get; }
}

public readonly struct AdapterResourceActionResult
{
    [JsonConstructor]
    public AdapterResourceActionResult(
        ulong requestId,
        ulong resourceKey,
        AdapterResourceActionMask action,
        AdapterResourceActionStatus status,
        AdapterResourceTier previousTier,
        AdapterResourceTier currentTier,
        ulong releasedBytes,
        ulong residentBytes,
        ushort detailCode = 0)
    {
        RequestId = requestId;
        ResourceKey = resourceKey;
        Action = action;
        Status = status;
        PreviousTier = previousTier;
        CurrentTier = currentTier;
        ReleasedBytes = releasedBytes;
        ResidentBytes = residentBytes;
        DetailCode = detailCode;
    }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong RequestId { get; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong ResourceKey { get; }
    public AdapterResourceActionMask Action { get; }
    public AdapterResourceActionStatus Status { get; }
    public AdapterResourceTier PreviousTier { get; }
    public AdapterResourceTier CurrentTier { get; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong ReleasedBytes { get; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong ResidentBytes { get; }
    public ushort DetailCode { get; }

    public static AdapterResourceActionResult Completed(
        in AdapterResourceActionRequest request,
        AdapterResourceTier previousTier,
        AdapterResourceTier currentTier,
        ulong releasedBytes,
        ulong residentBytes,
        ushort detailCode = 0)
    {
        return new AdapterResourceActionResult(
            request.RequestId,
            request.ResourceKey,
            request.Action,
            AdapterResourceActionStatus.Completed,
            previousTier,
            currentTier,
            releasedBytes,
            residentBytes,
            detailCode);
    }

    public static AdapterResourceActionResult Error(
        in AdapterResourceActionRequest request,
        AdapterResourceActionStatus status,
        ushort detailCode = 0)
    {
        return new AdapterResourceActionResult(
            request.RequestId,
            request.ResourceKey,
            request.Action,
            status,
            default,
            default,
            0,
            0,
            detailCode);
    }
}

public delegate AdapterResourceActionResult AdapterResourceActionHandler(in AdapterResourceActionRequest request);

public readonly struct AdapterResourceActionRegistration
{
    public AdapterResourceActionRegistration(
        ulong resourceKey,
        AdapterResourceActionMask supportedActions,
        AdapterResourceActionHandler handler)
    {
        if (resourceKey == 0)
        {
            throw new ArgumentException("Resource key must be nonzero.", nameof(resourceKey));
        }
        if (supportedActions == AdapterResourceActionMask.None)
        {
            throw new ArgumentException("At least one supported action is required.", nameof(supportedActions));
        }
        if ((supportedActions & ~AdapterResourceProtocol.AllActions) != 0)
        {
            throw new ArgumentException("Supported actions contain unknown bits.", nameof(supportedActions));
        }
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        ResourceKey = resourceKey;
        SupportedActions = supportedActions;
        Handler = handler;
    }

    public ulong ResourceKey { get; }
    public AdapterResourceActionMask SupportedActions { get; }
    public AdapterResourceActionHandler Handler { get; }
}

public sealed class AdapterResourceActionDispatcher
{
    private readonly object gate = new();
    private Dictionary<ulong, AdapterResourceActionRegistration> registrations = new();

    public int Count => Volatile.Read(ref registrations).Count;

    public void Register(AdapterResourceActionRegistration registration)
    {
        lock (gate)
        {
            var next = new Dictionary<ulong, AdapterResourceActionRegistration>(registrations)
            {
                [registration.ResourceKey] = registration
            };
            Volatile.Write(ref registrations, next);
        }
    }

    public void Register(
        ulong resourceKey,
        AdapterResourceActionMask supportedActions,
        AdapterResourceActionHandler handler)
    {
        Register(new AdapterResourceActionRegistration(resourceKey, supportedActions, handler));
    }

    public bool Unregister(ulong resourceKey)
    {
        if (resourceKey == 0)
        {
            return false;
        }

        lock (gate)
        {
            if (!registrations.ContainsKey(resourceKey))
            {
                return false;
            }

            var next = new Dictionary<ulong, AdapterResourceActionRegistration>(registrations);
            next.Remove(resourceKey);
            Volatile.Write(ref registrations, next);
            return true;
        }
    }

    public AdapterResourceActionResult Execute(in AdapterResourceActionRequest request)
    {
        if (request.ResourceKey == 0 || !IsSingleKnownAction(request.Action))
        {
            return AdapterResourceActionResult.Error(request, AdapterResourceActionStatus.InvalidRequest);
        }

        var snapshot = Volatile.Read(ref registrations);
        if (!snapshot.TryGetValue(request.ResourceKey, out var registration))
        {
            return AdapterResourceActionResult.Error(request, AdapterResourceActionStatus.ResourceNotFound);
        }

        if ((registration.SupportedActions & request.Action) == 0)
        {
            return AdapterResourceActionResult.Error(request, AdapterResourceActionStatus.ActionNotSupported);
        }

        return registration.Handler(in request);
    }

    private static bool IsSingleKnownAction(AdapterResourceActionMask action)
    {
        var value = (byte)action;
        return value != 0
            && (value & (value - 1)) == 0
            && (action & ~AdapterResourceProtocol.AllActions) == 0;
    }
}

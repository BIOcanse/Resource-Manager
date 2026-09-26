using System.Text;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

internal enum NativePortableSoftwarePayloadKind : byte
{
    SoftwareId = 1,
    CatalogEntryId = 2,
    DisplayName = 3,
    SoftwareKind = 4,
    ExecutablePath = 5,
    RootPath = 6
}

internal sealed record NativePortableSoftwarePayload(
    ulong Handle,
    NativePortableSoftwarePayloadKind Kind,
    string Text,
    ReadOnlyMemory<byte> Utf8Bytes);

internal readonly record struct NativePortableSoftwarePayloadRequest(
    NativePortableSoftwarePayloadKind Kind,
    string Text);

internal sealed class NativePortableSoftwareRegistryPayloadCatalog
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object stateGate = new();
    private readonly Dictionary<PayloadKey, ulong> handleByPayload = [];
    private readonly Dictionary<ulong, NativePortableSoftwarePayload> payloadByHandle = [];
    private ulong nextHandle;
    private bool exhausted;

    public NativePortableSoftwareRegistryPayloadCatalog(ulong firstHandle)
    {
        if (firstHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstHandle));
        }

        nextHandle = firstHandle;
    }

    public ulong GetOrAdd(NativePortableSoftwarePayloadKind kind, string text)
    {
        return GetOrAddBatch([new NativePortableSoftwarePayloadRequest(kind, text)])[0];
    }

    public ulong[] GetOrAddBatch(IReadOnlyList<NativePortableSoftwarePayloadRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("Portable software payload batch cannot be empty.", nameof(requests));
        }

        var validated = new ValidatedPayloadRequest[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            validated[index] = ValidateRequest(requests[index], nameof(requests));
        }

        lock (stateGate)
        {
            var missing = new List<ValidatedPayloadRequest>();
            var missingKeys = new HashSet<PayloadKey>();
            foreach (var request in validated)
            {
                if (!handleByPayload.ContainsKey(request.Key) && missingKeys.Add(request.Key))
                {
                    missing.Add(request);
                }
            }

            if (missing.Count > 0
                && (exhausted || (ulong)(missing.Count - 1) > ulong.MaxValue - nextHandle))
            {
                throw new InvalidOperationException("Portable software payload handle space is exhausted.");
            }

            foreach (var request in missing)
            {
                var handle = nextHandle;
                if (nextHandle == ulong.MaxValue)
                {
                    exhausted = true;
                }
                else
                {
                    nextHandle++;
                }

                var payload = new NativePortableSoftwarePayload(
                    handle,
                    request.Kind,
                    request.Text,
                    request.Utf8Bytes);
                handleByPayload.Add(request.Key, handle);
                payloadByHandle.Add(handle, payload);
            }

            var handles = new ulong[validated.Length];
            for (var index = 0; index < validated.Length; index++)
            {
                handles[index] = handleByPayload[validated[index].Key];
            }

            return handles;
        }
    }

    public bool TryResolve(
        NativePortableSoftwarePayloadKind expectedKind,
        ulong handle,
        out NativePortableSoftwarePayload? payload)
    {
        ValidateKind(expectedKind);
        if (handle == 0)
        {
            payload = null;
            return false;
        }

        lock (stateGate)
        {
            if (payloadByHandle.TryGetValue(handle, out var candidate) && candidate.Kind == expectedKind)
            {
                payload = candidate;
                return true;
            }
        }

        payload = null;
        return false;
    }

    public NativePortableSoftwarePayload ResolveRequired(
        NativePortableSoftwarePayloadKind expectedKind,
        ulong handle)
    {
        return TryResolve(expectedKind, handle, out var payload)
            ? payload!
            : throw new KeyNotFoundException(
                $"Portable software payload handle {handle} is not registered as {expectedKind}.");
    }

    public int Count
    {
        get
        {
            lock (stateGate)
            {
                return payloadByHandle.Count;
            }
        }
    }

    public bool IsExhausted
    {
        get
        {
            lock (stateGate)
            {
                return exhausted;
            }
        }
    }

    private static void ValidateKind(NativePortableSoftwarePayloadKind kind)
    {
        if (kind is < NativePortableSoftwarePayloadKind.SoftwareId or > NativePortableSoftwarePayloadKind.RootPath)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static ValidatedPayloadRequest ValidateRequest(
        NativePortableSoftwarePayloadRequest request,
        string parameterName)
    {
        ValidateKind(request.Kind);
        if (string.IsNullOrEmpty(request.Text))
        {
            throw new ArgumentException("Portable software payload cannot be empty.", parameterName);
        }

        if (request.Text.AsSpan().Contains('\0'))
        {
            throw new ArgumentException("Portable software payload cannot contain NUL.", parameterName);
        }

        try
        {
            return new ValidatedPayloadRequest(
                new PayloadKey(request.Kind, request.Text),
                request.Kind,
                request.Text,
                StrictUtf8.GetBytes(request.Text));
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Portable software payload must be valid Unicode.", parameterName, ex);
        }
    }

    private readonly record struct PayloadKey(
        NativePortableSoftwarePayloadKind Kind,
        string Text);

    private sealed record ValidatedPayloadRequest(
        PayloadKey Key,
        NativePortableSoftwarePayloadKind Kind,
        string Text,
        ReadOnlyMemory<byte> Utf8Bytes);
}

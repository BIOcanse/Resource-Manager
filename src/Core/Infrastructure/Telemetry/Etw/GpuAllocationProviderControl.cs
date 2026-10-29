using System.Buffers.Binary;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// The enable and capture commands both replace ETW's filters. Keep their
// native arguments and diagnostics together at the session boundary.
internal sealed class GpuAllocationProviderControl(
    EtwEventDropSession drops,
    Action<Guid, ulong, TraceEventProviderOptions?> enable,
    Action<Guid> disable,
    Action<Guid, ulong, int, byte[]?> captureState,
    string? sessionName = null)
{
    internal const int EventIdFilterType = unchecked((int)0x80000200);
    private readonly HashSet<Guid> enabled = [];

    internal void Enable(Guid provider, ulong keywords, int[]? eventIds)
    {
        Observe("enable", provider, () => enable(provider, keywords,
            eventIds is null ? null : new TraceEventProviderOptions { EventIDsToEnable = eventIds }));
        enabled.Add(provider);
        drops.EnableProvider(provider.ToString("D"), keywords);
    }

    internal void CaptureState(Guid provider, ulong keywords, int[]? eventIds)
    {
        // TraceEvent's CaptureState overload does not preserve the options
        // from EnableProvider. Supplying no descriptor removes that filter.
        Observe("capture-state", provider, () => captureState(provider, keywords, eventIds is null ? 0 : EventIdFilterType,
            eventIds is null ? null : CreateEventIdFilter(eventIds)));
    }

    internal static byte[] CreateEventIdFilter(int[] eventIds)
    {
        if (eventIds.Length is < 1 or > 64 || eventIds.Any(id => id is < 0 or > ushort.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(eventIds));
        // EVENT_FILTER_EVENT_ID: BOOLEAN FilterIn, UCHAR Reserved,
        // USHORT Count, followed by Count USHORT event IDs.
        var data = new byte[4 + eventIds.Length * sizeof(ushort)];
        data[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)eventIds.Length);
        for (var index = 0; index < eventIds.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4 + index * sizeof(ushort)), (ushort)eventIds[index]);
        return data;
    }

    internal void Disable(Guid provider)
    {
        Observe("disable", provider, () => disable(provider));
        enabled.Remove(provider);
        drops.DisableProvider(provider.ToString("D"));
    }

    internal void RecordSessionReleased()
    {
        foreach (var provider in enabled) drops.DisableProvider(provider.ToString("D"));
        enabled.Clear();
    }

    private void Observe(string operation, Guid provider, Action command)
    {
        GpuAllocationObservation.RecordCommand(sessionName, operation, provider, false, null);
        var succeeded = false;
        try { command(); succeeded = true; }
        finally { GpuAllocationObservation.RecordCommand(sessionName, operation, provider, true, succeeded); }
    }
}

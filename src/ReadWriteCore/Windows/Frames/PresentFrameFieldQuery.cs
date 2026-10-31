using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.ReadWrite;

namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>Nearest-rank percentile required by the derived frame-time field.</summary>
public enum FrameTimePercentile
{
    P50 = 50,
    P95 = 95,
    P99 = 99
}

/// <summary>
/// One immutable query over an exact process instance and original Present stream.
/// Its window cannot exceed the original ledger's retention.
/// </summary>
public sealed record PresentFrameFieldQuery
{
    public int ProcessId { get; }
    public long ProcessStartFileTimeUtc { get; }
    public FramePresentSource Source { get; }
    public ulong SwapChain { get; }
    public TimeSpan Window { get; }
    public FrameTimePercentile Percentile { get; }

    public PresentFrameFieldQuery(int processId, long processStartFileTimeUtc,
        FramePresentSource source, ulong swapChain, TimeSpan window, FrameTimePercentile percentile)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (processStartFileTimeUtc <= 0) throw new ArgumentOutOfRangeException(nameof(processStartFileTimeUtc));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        // The upper bound is the borrowed source's retention, checked when an owner binds this query.
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        if (!Enum.IsDefined(percentile)) throw new ArgumentOutOfRangeException(nameof(percentile));
        ProcessId = processId;
        ProcessStartFileTimeUtc = processStartFileTimeUtc;
        Source = source;
        SwapChain = swapChain;
        Window = window;
        Percentile = percentile;
    }

    public string ProcessIdentity => string.Create(CultureInfo.InvariantCulture,
        $"windows-process|{ProcessId}:{ProcessStartFileTimeUtc:X16}");
    public string PresentStreamIdentity => string.Create(CultureInfo.InvariantCulture,
        $"windows-present-stream|{ProcessId}:{ProcessStartFileTimeUtc:X16}|{(int)Source}:{SwapChain:X16}|{Window.Ticks}|P{(int)Percentile}");
    public string FrameStreamIdentity => string.Create(CultureInfo.InvariantCulture,
        $"windows-frame-stream|{ProcessId}:{ProcessStartFileTimeUtc:X16}|{(int)Source}:{SwapChain:X16}|{Window.Ticks}|P{(int)Percentile}");
    public string PresentWindowIdentity => string.Create(CultureInfo.InvariantCulture,
        $"windows-present-window|{ProcessId}:{ProcessStartFileTimeUtc:X16}|{(int)Source}:{SwapChain:X16}|{Window.Ticks}|P{(int)Percentile}");
    public string FrameWindowIdentity => string.Create(CultureInfo.InvariantCulture,
        $"windows-frame-window|{ProcessId}:{ProcessStartFileTimeUtc:X16}|{(int)Source}:{SwapChain:X16}|{Window.Ticks}|P{(int)Percentile}");

    public ImmutableArray<FieldReference> Fields =>
        [.. FrameFieldRegistry.Definitions.Select(definition => new FieldReference(DeviceIdentityFor(definition), definition.Name))];

    public FieldReference Field(string name)
    {
        var definition = FrameFieldRegistry.Find(name)
            ?? throw new ArgumentException("Unknown canonical frame field.", nameof(name));
        return new(DeviceIdentityFor(definition), definition.Name);
    }

    internal string DeviceIdentityFor(ResourceManager.ReadWrite.Information.DeviceInformationDefinition definition)
        => FrameFieldRegistry.ObjectType(definition) switch
        {
            "present_stream" => PresentStreamIdentity,
            "frame_stream" => FrameStreamIdentity,
            "present_stream_window" => PresentWindowIdentity,
            "frame_window" => FrameWindowIdentity,
            _ => throw new ArgumentException("Unknown frame object type.", nameof(definition))
        };

    internal bool Matches(FieldReference field, ResourceManager.ReadWrite.Information.DeviceInformationDefinition definition)
        => field.FieldName == definition.Name && field.DeviceIdentity == DeviceIdentityFor(definition);
}

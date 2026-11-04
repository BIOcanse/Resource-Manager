using System.Globalization;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

/// <summary>One channel and absolute bounded history. AfterRecordId belongs to
/// this channel only; caller must reset it after a log clear. No implicit cursor.</summary>
public sealed record WindowsWheaHistoryRequest
{
    public const string Provider = "Microsoft-Windows-WHEA-Logger";
    public string Channel { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ThroughUtc { get; }
    public int MaximumEvents { get; }
    public TimeSpan ReadBudget { get; }
    public int MaximumRenderedEventBytes { get; }
    public ulong? AfterRecordId { get; }
    public WindowsWheaHistoryRequest(string channel, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        int maximumEvents, TimeSpan readBudget, int maximumRenderedEventBytes = 1024 * 1024, ulong? afterRecordId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        if (channel.Length > 256 || channel.Any(static c => char.IsControl(c) || c is '\'' or '"' or '<' or '>'))
            throw new ArgumentException("Select one local channel name.", nameof(channel));
        if (throughUtc <= fromUtc || throughUtc - fromUtc > TimeSpan.FromDays(31)) throw new ArgumentOutOfRangeException(nameof(throughUtc));
        if (maximumEvents is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        if (readBudget <= TimeSpan.Zero || readBudget > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(readBudget));
        if (maximumRenderedEventBytes is < 4096 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumRenderedEventBytes));
        Channel = channel; FromUtc = fromUtc.ToUniversalTime(); ThroughUtc = throughUtc.ToUniversalTime();
        MaximumEvents = maximumEvents; ReadBudget = readBudget; MaximumRenderedEventBytes = maximumRenderedEventBytes; AfterRecordId = afterRecordId;
    }
    internal string XPath => $"*[System[Provider[@Name='{Provider}'] and TimeCreated[@SystemTime>='{FromUtc.ToString("O", CultureInfo.InvariantCulture)}' and @SystemTime<='{ThroughUtc.ToString("O", CultureInfo.InvariantCulture)}']{(AfterRecordId is { } id ? " and EventRecordID>" + id.ToString(CultureInfo.InvariantCulture) : "")}]]";
}

public enum WindowsWheaSeverity : uint { Recoverable = 0, Fatal = 1, Corrected = 2, Informational = 3 }

public sealed record WindowsWheaEvent(string Channel, ulong EventRecordId, DateTimeOffset? EventTimeUtc,
    System.Collections.Immutable.ImmutableArray<byte>? RawRecord, WindowsWheaSeverity? Severity)
{
    public string DeviceIdentity => $"windows-whea-event|{Uri.EscapeDataString(Channel)}|{WindowsWheaHistoryRequest.Provider}|{EventRecordId.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record WindowsWheaHistoryCapture(bool Available, IReadOnlyList<WindowsWheaEvent> Events,
    bool LimitReached, string? Error);

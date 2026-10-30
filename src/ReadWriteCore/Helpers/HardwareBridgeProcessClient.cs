using System.Diagnostics;
using System.Text.Json;

namespace ResourceManager.ReadWrite.Helpers;

/// <summary>Business facade. Requests never start or retry a process.</summary>
public class HardwareBridgeProcessClient : IHardwareBridgeProcessClient
{
    public HardwareBridgeProcessOwner Owner { get; }
    public HardwareBridgeProcessClient(Func<string?> resolveExecutablePath, HardwareBridgeProcessOptions options,
        Action<Exception?, string>? warning = null, Func<ProcessStartInfo, Process?>? startFactory = null)
        => Owner = new(resolveExecutablePath, options, warning, startFactory);
    public bool IsInstalled => Owner.IsInstalled;
    public string? UnavailableReason => Owner.UnavailableReason;
    public Task<JsonElement?> SendAsync(string operation, IReadOnlyDictionary<string, object?>? arguments, CancellationToken token)
        => Owner.SendWhileRunningAsync(operation, arguments, token);
    public HardwareBridgeReadingsSnapshot? ReadCurrentReadings() => Owner.ReadCurrentReadings();
    public IDisposable SubscribeReadings() => Owner.SubscribeReadings();
    public ValueTask<IAsyncDisposable> SuspendForInstallationAsync(CancellationToken token)
        => Owner.SuspendForInstallationAsync(token);
    public void Dispose() => Owner.Dispose();
}

/// <summary>The product passes every timing from its hardware_bridge freedom points; this library holds no second default.</summary>
public sealed record HardwareBridgeProcessOptions(TimeSpan RequestTimeout,
    TimeSpan DiagnosticDrainTimeout, HelperErrorOutputOptions ErrorOutput)
{
    public required TimeSpan StopTimeout { get; init; }
    public TimeSpan AvailabilityCheckInterval { get; init; } = TimeSpan.Zero;
    public Func<bool>? IsRuntimeAvailable { get; init; }
    public required TimeSpan ReadingInterval { get; init; }
}

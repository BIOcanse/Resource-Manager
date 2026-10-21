using System.Text.Json;

namespace ResourceManager.ReadWrite.Vendors.Amd;

/// <summary>
/// Original ADLX control transaction and persisted first-baseline owner. Hardware is explicitly borrowed;
/// disposal drains this gate without disposing it or terminating the shared native ADLX driver.
/// Construct with new AdlxTuningHardware() and an explicit baseline path for real independent consumption.
/// Probe/Read collect only on request; setting writes and restoration occur only on explicit calls.
/// </summary>
public sealed class AmdDesktopGpuControlResource : IDisposable
{
    private readonly IAdlxTuningHardware hardware;
    private readonly string baselinePath;
    private readonly object gate = new();
    private Dictionary<string, int>? baselines;
    private bool disposed;
    public AmdDesktopGpuControlResource(IAdlxTuningHardware hardware, AmdDesktopGpuControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaselinePath);
        this.hardware = hardware;
        baselinePath = Path.GetFullPath(options.BaselinePath);
    }
    public static bool IsAdmitted(AmdDesktopGpuControlTarget target, AmdDesktopGpuControlField field)
        => target.Route == AmdDesktopGpuControlRoute.WindowsAmdFixedDiscrete && (int)field is >= 0 and <= 9
            && (target.Kind == AmdDesktopGpuControlObjectKind.Gpu ? (int)field is <= 4 or >= 8
                : target.Kind == AmdDesktopGpuControlObjectKind.Fan && (int)field is >= 5 and <= 7);
    public AmdDesktopGpuControlAvailability Probe(AmdDesktopGpuControlTarget target, AmdDesktopGpuControlField field)
    {
        lock (gate)
        {
            CheckLifetime();
            if (!IsAdmitted(target, field)) return new(false, false);
            try
            {
                var value = ReadHardware(target, field);
                return new(true, true, new(value.Minimum, value.Maximum, value.Step, AmdDesktopGpuControlFormatter.Unit(field)))
                    { GfxMode = value.GfxMode };
            }
            catch (Exception error) when (Unavailable(error))
            {
                return new(true, false, FailureKind: error is AdlxUnknownGenerationException ? AmdDesktopGpuControlFailureKind.NotImplemented
                    : error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                        ? AmdDesktopGpuControlFailureKind.Component : AmdDesktopGpuControlFailureKind.Platform, Error: error.Message);
            }
        }
    }
    public Task<AmdDesktopGpuControlOutcome> WriteAsync(AmdDesktopGpuControlTarget target, AmdDesktopGpuControlField field,
        AmdDesktopGpuControlSetting setting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckLifetime();
            if (!IsAdmitted(target, field)) return Task.FromResult(new AmdDesktopGpuControlOutcome(AmdDesktopGpuControlStatus.Unsupported, AmdDesktopGpuControlMessage.RouteRejected));
            try
            {
                var before = ReadHardware(target, field);
                var requested = field == AmdDesktopGpuControlField.FanZeroRpm
                    ? setting.Toggle is { } enabled ? enabled ? 1d : 0d : (double?)null : setting.Number;
                if (requested is not { } number || !double.IsFinite(number) || number != Math.Truncate(number)
                    || number < before.Minimum || number > before.Maximum || (number - before.Minimum) % before.Step != 0)
                    return Task.FromResult(new AmdDesktopGpuControlOutcome(AmdDesktopGpuControlStatus.Failed, AmdDesktopGpuControlMessage.InvalidRangeOrStep));
                CaptureBaseline(before.Pnp, (int)field, before.Current);
                hardware.Write(before.Pnp, (int)field, (int)number);
                var after = ReadHardware(target, field);
                var applied = after.Pnp == before.Pnp && after.Current == (int)number;
                return Task.FromResult(new AmdDesktopGpuControlOutcome(applied ? AmdDesktopGpuControlStatus.Applied : AmdDesktopGpuControlStatus.Failed,
                    applied ? AmdDesktopGpuControlMessage.Applied : AmdDesktopGpuControlMessage.ReadbackMismatch));
            }
            catch (Exception error) when (Unavailable(error))
            { return Task.FromResult(new AmdDesktopGpuControlOutcome(AmdDesktopGpuControlStatus.Failed, AmdDesktopGpuControlMessage.HardwareFailure, error.Message)); }
        }
    }
    public Task<AmdDesktopGpuControlValue?> ReadAsync(AmdDesktopGpuControlTarget target, AmdDesktopGpuControlField field,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckLifetime();
            if (!IsAdmitted(target, field)) return Task.FromResult<AmdDesktopGpuControlValue?>(null);
            try
            {
                var value = ReadHardware(target, field);
                return Task.FromResult<AmdDesktopGpuControlValue?>(field == AmdDesktopGpuControlField.FanZeroRpm
                    ? new(value.Pnp, Toggle: value.Current != 0) { GfxMode = value.GfxMode }
                    : new(value.Pnp, Number: value.Current, Unit: AmdDesktopGpuControlFormatter.Unit(field)) { GfxMode = value.GfxMode });
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult<AmdDesktopGpuControlValue?>(null); }
        }
    }
    public Task<bool> RestoreAsync(AmdDesktopGpuControlTarget target, AmdDesktopGpuControlField field,
        IReadOnlySet<AmdDesktopGpuControlField> stillConfigured, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckLifetime();
            if (!IsAdmitted(target, field)) return Task.FromResult(false);
            try
            {
                LoadBaselines();
                var current = ReadHardware(target, field);
                if (!baselines!.TryGetValue(Key(current.Pnp, (int)field), out var baseline)) return Task.FromResult(false);
                if (field == AmdDesktopGpuControlField.CoreClockMinimum && current.GfxMode == AdlxGfxMode.Unknown) return Task.FromResult(false);
                if (field is AmdDesktopGpuControlField.CoreClockMinimum or AmdDesktopGpuControlField.CoreClockMaximum && current.GfxMode == AdlxGfxMode.Absolute)
                {
                    var siblingField = field == AmdDesktopGpuControlField.CoreClockMinimum ? AmdDesktopGpuControlField.CoreClockMaximum : AmdDesktopGpuControlField.CoreClockMinimum;
                    var sibling = ReadHardware(target, siblingField);
                    if (sibling.Pnp != current.Pnp) return Task.FromResult(false);
                    if (field == AmdDesktopGpuControlField.CoreClockMinimum ? baseline > sibling.Current : baseline < sibling.Current)
                    {
                        if (stillConfigured.Contains(siblingField) || !baselines.TryGetValue(Key(current.Pnp, (int)siblingField), out var siblingBaseline)) return Task.FromResult(false);
                        // Restore the blocking sibling first, retaining its baseline for its own verified release.
                        hardware.Write(current.Pnp, (int)siblingField, siblingBaseline);
                        var verifiedSibling = ReadHardware(target, siblingField);
                        if (verifiedSibling.Pnp != current.Pnp || verifiedSibling.Current != siblingBaseline) return Task.FromResult(false);
                    }
                }
                hardware.Write(current.Pnp, (int)field, baseline);
                var after = ReadHardware(target, field);
                var restored = after.Pnp == current.Pnp && after.Current == baseline;
                if (restored)
                {
                    var next = new Dictionary<string, int>(baselines!);
                    next.Remove(Key(current.Pnp, (int)field));
                    SaveBaselines(next);
                }
                return Task.FromResult(restored);
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult(false); }
        }
    }
    private AdlxTuningValue ReadHardware(AmdDesktopGpuControlTarget target, AmdDesktopGpuControlField field)
    {
        var value = hardware.Read(target.AdapterIndex, (int)field);
        if (target.ExpectedPnp is { } expected && !string.Equals(expected, value.Pnp, StringComparison.OrdinalIgnoreCase))
            throw new IOException("adlx-control-pnp-mismatch");
        if (value.Minimum > value.Maximum || value.Step <= 0) throw new IOException("adlx-control-invalid-range");
        return value;
    }
    private static string Key(string pnp, int field) => $"{pnp.ToUpperInvariant()}\n{field}";
    private void LoadBaselines() => baselines ??= File.Exists(baselinePath)
        ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(baselinePath)) ?? [] : [];
    private void CaptureBaseline(string pnp, int field, int value)
    {
        LoadBaselines();
        var key = Key(pnp, field);
        if (baselines!.ContainsKey(key)) return;
        SaveBaselines(new Dictionary<string, int>(baselines) { [key] = value });
    }
    private void SaveBaselines(Dictionary<string, int> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        File.WriteAllText(baselinePath + ".tmp", JsonSerializer.Serialize(next));
        File.Move(baselinePath + ".tmp", baselinePath, overwrite: true);
        baselines = next;
    }
    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException;
    private void CheckLifetime() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { lock (gate) disposed = true; }
}

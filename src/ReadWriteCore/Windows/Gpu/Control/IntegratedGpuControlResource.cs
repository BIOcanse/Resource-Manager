using System.Text.Json;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Intel;


namespace ResourceManager.ReadWrite.Windows.Gpu.Control;
public sealed class IntegratedGpuControlResource : IDisposable
{
    internal const string CurveOptimizerCapabilityId = "gpu.curve-optimizer";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";

    private readonly IIntelGraphicsControlHardware intel;
    private readonly IHardwareBridgeProcessClient? bridge;
    private readonly Func<CancellationToken,Task<bool>> isOverclockAccepted;
    private readonly Func<IntegratedGpuControlMessage,IReadOnlyList<object?>,string>? describe;
    private readonly Action<Exception?,IntegratedGpuControlMessage,IReadOnlyList<object?>>? warning;
    private readonly IntegratedGpuControlBaselineStore amdBaseline;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int disposed;
    /// <summary>Borrow the same helper as AMD CPU. Intel hardware remains borrowed; consent is explicit read-only state.</summary>
    public IntegratedGpuControlResource(IHardwareBridgeProcessClient? bridge, IIntelGraphicsControlHardware intel, IntegratedGpuControlOptions options,
        Func<IntegratedGpuControlMessage,IReadOnlyList<object?>,string>? describe = null,
        Action<Exception?,IntegratedGpuControlMessage,IReadOnlyList<object?>>? warning = null)
    {
        ArgumentNullException.ThrowIfNull(intel); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.IsOverclockAccepted);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AmdBaselinePath);
        this.bridge=bridge;this.intel=intel;this.isOverclockAccepted=options.IsOverclockAccepted;
        this.describe=describe;this.warning=warning;
        amdBaseline=new(Path.GetFullPath(options.AmdBaselinePath));
    }
    /// <summary>Borrows a caller-owned client. The caller explicitly runs and joins its process owner.</summary>
    public static IntegratedGpuControlResource Create(IHardwareBridgeProcessClient bridge, IntegratedGpuControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return new(bridge, new IntelGraphicsControlBridge(), options);
    }
    private string Describe(IntegratedGpuControlMessage code,params object?[] args)
        => describe?.Invoke(code,args) ?? "integrated-gpu-control-"+code+(args.Length == 0 ? "" : ": "+string.Join(", ",args));
    private void Warn(Exception? error,IntegratedGpuControlMessage code,params object?[] args) => warning?.Invoke(error,code,args);
    private void CheckLifetime() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0,this);
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    public async Task<bool> RestoreAsync(IntegratedGpuControlTarget target,IntegratedGpuControlCapability capability,CancellationToken token=default)
    {
        CheckLifetime();
        token.ThrowIfCancellationRequested();
        double? original;
        if (target.Route == IntegratedGpuControlRoute.AmdIntegrated)
        {
            try { if (!amdBaseline.TryRead(target.Id, out original)) return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Warn(error, IntegratedGpuControlMessage.BaselineWarning); return false; }
        }
        else original = capability.Range?.DefaultValue;
        if (original is not { } value) return false;
        return (await WriteAsync(target,capability,new(capability.Field,value,capability.Range?.Unit),token).ConfigureAwait(false)).Status == IntegratedGpuControlStatus.Applied;
    }
    private string BridgeMissing => Describe(IntegratedGpuControlMessage.BridgeMissing);
    private string BridgeSilent => Describe(IntegratedGpuControlMessage.BridgeSilent);
    private string NotSupported => Describe(IntegratedGpuControlMessage.NotSupported);
    private string NotEffective => Describe(IntegratedGpuControlMessage.NotEffective);
    private string IntelDriverMissing => Describe(IntegratedGpuControlMessage.IntelDriverMissing);
    private string IntelTargetMissing => Describe(IntegratedGpuControlMessage.IntelTargetMissing);
    private string OverclockNotAccepted => Describe(IntegratedGpuControlMessage.OverclockNotAccepted);
    public string? ChannelOf(IntegratedGpuControlTarget target, string capabilityId)
        => !IsMine(target, capabilityId) ? null : capabilityId switch
    {
        CurveOptimizerCapabilityId => "amd-smu",
        CoreClockOffsetCapabilityId => "igcl",
        _ => null
    };

    public IntegratedGpuControlAvailability Probe(IntegratedGpuControlTarget target, IntegratedGpuControlCapability capability)
        => Probe(target, capability, CancellationToken.None);

    public IntegratedGpuControlAvailability Probe(IntegratedGpuControlTarget target, IntegratedGpuControlCapability capability, CancellationToken cancellationToken)
    {
        CheckLifetime();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return IntegratedGpuControlAvailability.NotMine;
        }
        if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
        {
            return ProbeIntel(target, cancellationToken);
        }
        if (bridge is null || !bridge.IsInstalled)
        {
            return IntegratedGpuControlAvailability.No(BridgeMissing, kind: IntegratedGpuControlUnavailableKind.Component);
        }

        var described = Ask("describe");
        if (described is not { } description || !Flag(description, "available"))
        {
            return IntegratedGpuControlAvailability.No(Text(described, "reason") ?? bridge?.UnavailableReason ?? BridgeSilent);
        }
        if (!Flag(description, "integratedGpuCurveOptimizer"))
        {
            return IntegratedGpuControlAvailability.No(NotSupported);
        }

        double? baseline;
        bool valid;
        try { valid = amdBaseline.TryRead(target.Id, out baseline); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warn(error,IntegratedGpuControlMessage.BaselineWarning);
            baseline = null;
            valid = false;
        }
        if (!valid)
            return IntegratedGpuControlAvailability.No(Describe(IntegratedGpuControlMessage.BaselineUnavailable), canRead: true);
        return IntegratedGpuControlAvailability.Yes(
            capability.Range is { } declared
                ? declared with { DefaultValue = baseline }
                : new IntegratedGpuControlRange(-30, 10, 1, "档", baseline));
    }

    public async Task<IntegratedGpuControlOutcome> WriteAsync(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        IntegratedGpuControlSetting setting,
        CancellationToken cancellationToken = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id) || setting.CapabilityId != capability.Id)
            return Failed(target, capability, NotSupported);
        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, Describe(IntegratedGpuControlMessage.NumberRequired));
        }
        var bounds = capability.Range;
        var wanted = bounds is null
            ? value
            : Math.Clamp(value, bounds.Minimum, bounds.Maximum);

        if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
        {
            var accepted = await isOverclockAccepted(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!accepted) return Failed(target, capability, OverclockNotAccepted);
        }
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
                return WriteIntel(target, capability, wanted, cancellationToken);
            try
            {
                if (!amdBaseline.TryRead(target.Id, out var baseline))
                    return Failed(target, capability, Describe(IntegratedGpuControlMessage.BaselineUnavailable));
                if (baseline is null)
                {
                    var current = bridge?.ReadCurrentReadings()?.IntegratedGpuCurveOptimizerCounts;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (amdBaseline.Capture(target.Id, current) is null)
                        return Failed(target, capability, Describe(IntegratedGpuControlMessage.BaselineUnavailable));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Warn(error, IntegratedGpuControlMessage.BaselineWarning);
                return Failed(target, capability, Describe(IntegratedGpuControlMessage.BaselineUnavailable));
            }
            var counts = (int)wanted;
            // 和处理器那几项一样，值连同单位一起发；辅助进程不收裸数字。
            var response = await SendAsync(
                "set-igpu-curve-optimizer",
                new Dictionary<string, object?> { ["value"] = counts, ["unit"] = "档" },
                cancellationToken).ConfigureAwait(false);
            if (response is not { } result || !Flag(result, "ok"))
            {
                return Failed(target, capability, BridgeSilent);
            }
            if (!Flag(result, "accepted"))
            {
                Warn(null,IntegratedGpuControlMessage.SmuRejectedWarning,counts);
                return Failed(target, capability, Describe(IntegratedGpuControlMessage.SmuRejected));
            }
            if (Number(result, "integratedGpuCurveOptimizerCounts") is { } after
                && (int)after != counts)
            {
                return Failed(target, capability, NotEffective);
            }

            return new IntegratedGpuControlOutcome(
                target.Id,
                capability.Id,
                IntegratedGpuControlStatus.Applied,
                null) { ReadbackVerified = Number(result, "integratedGpuCurveOptimizerCounts") is not null };
        }
        finally { writeGate.Release(); }
    }
    public async Task<IntegratedGpuControlValue?> ReadAsync(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        CancellationToken cancellationToken = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return null;
        // Intel 那条走 IGCL，和辅助进程没关系。
        if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var intelReading = intel.ReadFrequencyOffsetMhz(target.AdapterKey, cancellationToken);
                WarnRelease(intelReading.ReleaseErrorCode);
                return intelReading.OffsetMhz is { } offset && intelReading.Unreadable is null
                    ? new(target.Id, capability.Id, Number: offset, Unit: "MHz")
                    : new(target.Id, capability.Id, UnreadableReason: intelReading.Unreadable switch
                    {
                        IntelGraphicsControlUnreadable.DriverMissing => IntelDriverMissing,
                        IntelGraphicsControlUnreadable.TargetMissing => IntelTargetMissing,
                        _ => Describe(IntegratedGpuControlMessage.ReadingMissing)
                    });
            }
            catch (OperationCanceledException error)
            {
                WarnExceptionalRelease(error);
                throw;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                WarnExceptionalRelease(error);
                WarnNativeFailure(error);
                return new(target.Id, capability.Id, UnreadableReason: IntelDriverMissing);
            }
            catch (Exception error) when (IsNativeFailure(error))
            {
                WarnExceptionalRelease(error);
                WarnNativeFailure(error);
                return new(target.Id, capability.Id, UnreadableReason: Describe(IntegratedGpuControlMessage.ReadingMissing));
            }
            finally { writeGate.Release(); }
        }

        var description = await SendAsync("describe", null, cancellationToken).ConfigureAwait(false);
        if (description is not { } facts || !Flag(facts, "available") || !Flag(facts, "integratedGpuCurveOptimizer"))
            return new(target.Id, capability.Id, UnreadableReason: Text(description, "reason") ?? bridge?.UnavailableReason ?? BridgeSilent);
        var reading = bridge?.ReadCurrentReadings();
        if (reading is null)
        {
            return new IntegratedGpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: BridgeSilent);
        }
        return reading.Value.IntegratedGpuCurveOptimizerCounts is { } value
            ? new IntegratedGpuControlValue(
                target.Id,
                capability.Id,
                Number: Math.Round(value),
                Unit: "档")
            : new IntegratedGpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: Describe(IntegratedGpuControlMessage.ReadingMissing));
    }
    // Use the existing gate for the whole Intel operation, including close. Reads and
    // probes cannot observe a write between waiver/set/readback. No second lock order.
    private IntegratedGpuControlAvailability ProbeIntel(IntegratedGpuControlTarget target, CancellationToken token)
    {
        try
        {
            if (!intel.IsDriverAvailable)
                return IntegratedGpuControlAvailability.No(IntelDriverMissing, kind: IntegratedGpuControlUnavailableKind.Component);
            // DLL/export facts need no session. Consent must not hold the IGCL gate.
            if (!isOverclockAccepted(token).GetAwaiter().GetResult())
                return IntegratedGpuControlAvailability.No(OverclockNotAccepted, kind: IntegratedGpuControlUnavailableKind.Prerequisite);
            IntelGraphicsControlProbeResult probe;
            writeGate.Wait(token);
            try { probe = intel.Probe(target.AdapterKey, token); }
            finally { writeGate.Release(); }
            WarnRelease(probe.ReleaseErrorCode);
            return probe.Status switch
            {
                IntelGraphicsControlProbe.DriverMissing => IntegratedGpuControlAvailability.No(IntelDriverMissing, kind: IntegratedGpuControlUnavailableKind.Component),
                IntelGraphicsControlProbe.TargetMissing => IntegratedGpuControlAvailability.No(IntelTargetMissing),
                IntelGraphicsControlProbe.NotSupported => IntegratedGpuControlAvailability.No(NotSupported),
                _ => IntegratedGpuControlAvailability.Yes()
            };
        }
        catch (OperationCanceledException error)
        {
            WarnExceptionalRelease(error);
            throw;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            WarnExceptionalRelease(error);
            WarnNativeFailure(error);
            return IntegratedGpuControlAvailability.No(IntelDriverMissing, kind: IntegratedGpuControlUnavailableKind.Component);
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            WarnExceptionalRelease(error);
            WarnNativeFailure(error);
            return IntegratedGpuControlAvailability.No(Describe(IntegratedGpuControlMessage.DriverRejected));
        }
    }

    private IntegratedGpuControlOutcome WriteIntel(
        IntegratedGpuControlTarget target, IntegratedGpuControlCapability capability,
        double offsetMhz, CancellationToken cancellationToken)
    {
        try
        {
            var result = intel.WriteFrequencyOffsetMhz(target.AdapterKey, offsetMhz, cancellationToken);
            WarnRelease(result.ReleaseErrorCode);
            if (result.Failure is { } failure)
            {
                if (failure == IntelGraphicsControlWriteFailure.DriverRejected)
                    Warn(null, IntegratedGpuControlMessage.IntelRejectedWarning, offsetMhz, result.DriverResult);
                return Failed(target, capability, failure switch
                {
                    IntelGraphicsControlWriteFailure.DriverMissing => IntelDriverMissing,
                    IntelGraphicsControlWriteFailure.TargetMissing => IntelTargetMissing,
                    IntelGraphicsControlWriteFailure.WaiverRejected => Describe(IntegratedGpuControlMessage.WaiverRejected),
                    _ => Describe(IntegratedGpuControlMessage.DriverRejected)
                });
            }
            return result.DriverResult != 0 || result.ReadbackMhz is not { } applied || !double.IsFinite(applied) || Math.Abs(applied - offsetMhz) > 1
                ? Failed(target, capability, NotEffective)
                : new(target.Id, capability.Id, IntegratedGpuControlStatus.Applied, null) { ReadbackVerified = true };
        }
        catch (OperationCanceledException error)
        {
            WarnExceptionalRelease(error);
            throw;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            WarnExceptionalRelease(error);
            WarnNativeFailure(error);
            return Failed(target, capability, IntelDriverMissing);
        }
        catch (Exception error) when (IsNativeFailure(error))
        {
            // Work did not produce a receipt. Completed writes report release failures as data above.
            WarnExceptionalRelease(error);
            WarnNativeFailure(error);
            return Failed(target, capability, Describe(IntegratedGpuControlMessage.DriverRejected));
        }
    }

    private static bool IsNativeFailure(Exception error)
        => error is ExternalException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
    private void WarnNativeFailure(Exception error)
        => Warn(error, IntegratedGpuControlMessage.IntelRejectedWarning, "IGCL operation", error.HResult);
    private void WarnRelease(int? code)
    {
        if (code is { } result)
            Warn(new ExternalException("IGCL could not close the API session.", result), IntegratedGpuControlMessage.IntelReleaseWarning, result);
    }
    private void WarnExceptionalRelease(Exception error)
    {
        if (error.Data[IntelGraphicsControlBridge.ReleaseFailureKey] is ExternalException release)
            Warn(release, IntegratedGpuControlMessage.IntelReleaseWarning, release.ErrorCode);
    }
    private Task<JsonElement?> SendAsync(string operation,IReadOnlyDictionary<string,object?>? arguments,CancellationToken token)
        => bridge is null ? Task.FromResult<JsonElement?>(null) : bridge.SendAsync(operation,arguments,token);
    private JsonElement? Ask(string operation)
        => SendAsync(operation, null, CancellationToken.None).GetAwaiter().GetResult();

    private static bool Flag(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
                ? property.GetDouble()
                : null;

    private static string? Text(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool IsMine(IntegratedGpuControlTarget target,string capabilityId)
        => target.Route == IntegratedGpuControlRoute.AmdIntegrated && capabilityId == CurveOptimizerCapabilityId
            || target.Route == IntegratedGpuControlRoute.IntelIntegrated && capabilityId == CoreClockOffsetCapabilityId;

    private static IntegratedGpuControlOutcome Failed(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, IntegratedGpuControlStatus.Failed, reason);
}

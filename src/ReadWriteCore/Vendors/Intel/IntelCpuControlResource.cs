using System.Text.Json;

namespace ResourceManager.ReadWrite.Vendors.Intel;

/// <summary>Intel CPU control through the signed PawnIO module. Probing submits read queries only.</summary>
public sealed class IntelCpuControlResource : IDisposable
{
    private readonly Func<IIntelCpuHardware> open;
    private readonly string baselinePath;
    private readonly object gate = new();
    private IIntelCpuHardware? hardware;
    private Dictionary<string, ulong>? baselines;

    private bool disposed;
    private readonly Func<IntelCpuControlMessage, IReadOnlyList<object?>, string> describe;

    /// <summary>Every session returned by open is owned by this resource and closed under its original gate.</summary>
    public IntelCpuControlResource(Func<IIntelCpuHardware> open, string baselinePath,
        Func<IntelCpuControlMessage, IReadOnlyList<object?>, string>? formatter = null)
    {
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.baselinePath = baselinePath ?? throw new ArgumentNullException(nameof(baselinePath));
        describe = formatter ?? IntelCpuControlFormatter.Describe;
    }

    /// <summary>Real signed PawnIO execution, opened lazily only by an explicit operation. Installs nothing.</summary>
    public static IntelCpuControlResource CreateDefault(IntelCpuControlOptions options,
        Func<IntelCpuControlMessage, IReadOnlyList<object?>, string>? formatter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(() => new PawnIoIntelCpuHardware(options.ModulePath, options.LibraryPath), options.BaselinePath, formatter);
    }

    private string Describe(IntelCpuControlMessage message, params object?[] arguments) => describe(message, arguments);
    private void CheckLifetime() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
    public static int? VoltagePlane(string id) => id switch
    {
        "cpu.core-voltage-offset" => 0, "cpu.igpu-voltage-offset" => 1,
        "cpu.cache-voltage-offset" => 2, "cpu.system-agent-voltage-offset" => 3, _ => null
    };
    private static bool IsMine(IntelCpuControlTarget target, string capability)
        => target.IsWindowsIntelCpu && capability.StartsWith("cpu.", StringComparison.Ordinal);
    public string? ChannelOf(IntelCpuControlTarget target, string capabilityId) => IsMine(target, capabilityId) ? "intel.msr" : null;
    private static bool IsRapl(string id) => id is "cpu.power-limit" or "cpu.short-power-limit" or "cpu.power-limit-window" or "cpu.short-power-limit-window";
    private static bool ShortTerm(string id) => id.StartsWith("cpu.short-", StringComparison.Ordinal);
    private static bool IsWindow(string id) => id.EndsWith("-window", StringComparison.Ordinal);

    public IntelCpuControlAvailability Probe(IntelCpuControlTarget target, IntelCpuControlCapability capability)
    {
        CheckLifetime();
        if (!IsMine(target, capability.Id)) return IntelCpuControlAvailability.NotMine;
        if (!IsRapl(capability.Id) && VoltagePlane(capability.Id) is null) return IntelCpuControlAvailability.No(
            capability.Id == "cpu.power-limit-mmio"
                ? Describe(IntelCpuControlMessage.MmioWriteNotImplemented)
                : Describe(IntelCpuControlMessage.RegisterWriteNotImplemented),
            kind: "not-implemented");
        lock (gate)
        {
            CheckLifetime();
            try
            {
                var io = hardware ??= open();
                if (VoltagePlane(capability.Id) is { } plane)
                {
                    if (!io.CanWriteMsr(IntelOcMailbox.Register)) return IntelCpuControlAvailability.No(Describe(IntelCpuControlMessage.MailboxModuleUnavailable), kind: "component");
                    IntelOcMailbox.Read(io, plane);
                    return IntelCpuControlAvailability.Yes(capability.Range);
                }
                var limits = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((limits & (1UL << 63)) != 0) return IntelCpuControlAvailability.No(Describe(IntelCpuControlMessage.FirmwarePowerLimitLocked), canRead: true);
                if (!io.CanWriteMsr(IntelRaplEncoding.LimitRegister)) return IntelCpuControlAvailability.No(Describe(IntelCpuControlMessage.PowerLimitModuleUnavailable), kind: "component", canRead: true);
                var units = io.ReadMsr(IntelRaplEncoding.UnitsRegister);
                var maximum = IsWindow(capability.Id) ? IntelRaplEncoding.DecodeWindow(127, units) : 0x7fff * IntelRaplEncoding.PowerUnit(units);
                var declared = capability.Range;
                return IntelCpuControlAvailability.Yes(declared is null ? null : declared with { Maximum = Math.Min(declared.Maximum, maximum) });
            }
            catch (Exception error) when (Unavailable(error))
            { return IntelCpuControlAvailability.No(error.Message,
                kind: error is FileNotFoundException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                    ? "component" : "platform"); }
        }
    }

    public Task<IntelCpuControlOutcome> WriteAsync(IntelCpuControlTarget target, IntelCpuControlCapability capability, IntelCpuControlSetting setting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckLifetime();
        lock (gate)
        {
            CheckLifetime();
            try
            {
                var availability = Probe(target, capability);
                if (!availability.CanWrite) return Task.FromResult(Result(target, capability, "unsupported", availability.Reason));
                if (setting.Number is not { } number || !double.IsFinite(number)) return Task.FromResult(Result(target, capability, "failed", Describe(IntelCpuControlMessage.NumberRequired)));
                if (availability.Range is { } range && (number < range.Minimum || number > range.Maximum)) return Task.FromResult(Result(target, capability, "failed", Describe(IntelCpuControlMessage.OutOfRange)));
                var io = hardware!;
                if (VoltagePlane(capability.Id) is { } plane)
                {
                    var before = IntelOcMailbox.Read(io, plane);
                    // Change the offset field only; preserve the domain's voltage target/mode.
                    var encoded = (before & 0x1fffffU) | IntelOcMailbox.Encode(number);
                    CaptureBaseline(target, capability.Id, before);
                    IntelOcMailbox.Write(io, plane, encoded);
                    var after = IntelOcMailbox.Read(io, plane);
                    return Task.FromResult(Result(target, capability, after == encoded ? "applied" : "failed",
                        after == encoded ? Describe(IntelCpuControlMessage.VoltageVerified, IntelOcMailbox.Decode(after)) : Describe(IntelCpuControlMessage.VoltageReadbackMismatch)));
                }
                var units = io.ReadMsr(IntelRaplEncoding.UnitsRegister);
                var original = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((original & (1UL << 63)) != 0) return Task.FromResult(Result(target, capability, "failed", Describe(IntelCpuControlMessage.FirmwarePowerRegisterLocked)));
                var shortTerm = ShortTerm(capability.Id);
                var window = IsWindow(capability.Id);
                var mask = IntelRaplEncoding.FieldMask(window, shortTerm);
                CaptureBaseline(target, capability.Id, original & mask);
                var next = window ? IntelRaplEncoding.SetWindow(original, units, shortTerm, number) : IntelRaplEncoding.SetPower(original, units, shortTerm, number);
                io.WriteMsr(IntelRaplEncoding.LimitRegister, next);
                var readback = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((readback & mask) != (next & mask)) return Task.FromResult(Result(target, capability, "failed", Describe(IntelCpuControlMessage.PowerReadbackMismatch)));
                var actual = window ? IntelRaplEncoding.ReadWindow(readback, units, shortTerm) : IntelRaplEncoding.ReadPower(readback, units, shortTerm);
                return Task.FromResult(Result(target, capability, "applied", Describe(IntelCpuControlMessage.PowerVerified, actual, capability.Range?.Unit)));
            }
            catch (Exception error) when (Unavailable(error) || error is ArgumentOutOfRangeException)
            { return Task.FromResult(Result(target, capability, "failed", error.Message)); }
        }
    }

    public Task<IntelCpuControlActualValue?> ReadAsync(IntelCpuControlTarget target, IntelCpuControlCapability capability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckLifetime();
        if (!IsMine(target, capability.Id)) return Task.FromResult<IntelCpuControlActualValue?>(null);
        if (!IsRapl(capability.Id) && VoltagePlane(capability.Id) is null) return Task.FromResult<IntelCpuControlActualValue?>(null);
        lock (gate)
        {
            CheckLifetime();
            try
            {
                var io = hardware ??= open();
                if (VoltagePlane(capability.Id) is { } plane)
                    return Task.FromResult<IntelCpuControlActualValue?>(new(target.Id, capability.Id, Number: IntelOcMailbox.Decode(IntelOcMailbox.Read(io, plane)), Unit: "mV"));
                var units = io.ReadMsr(IntelRaplEncoding.UnitsRegister);
                var limits = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                var value = IsWindow(capability.Id) ? IntelRaplEncoding.ReadWindow(limits, units, ShortTerm(capability.Id)) : IntelRaplEncoding.ReadPower(limits, units, ShortTerm(capability.Id));
                return Task.FromResult<IntelCpuControlActualValue?>(new(target.Id, capability.Id, Number: value, Unit: capability.Range?.Unit));
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult<IntelCpuControlActualValue?>(null); }
        }
    }

    public Task<bool> RestoreAsync(IntelCpuControlTarget target, IntelCpuControlCapability capability, IReadOnlySet<string> stillConfigured, CancellationToken cancellationToken, IntelCpuControlSetting? previousSetting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckLifetime();
        if (!IsMine(target, capability.Id)) return Task.FromResult(false);
        lock (gate)
        {
            CheckLifetime();
            try
            {
                LoadBaselines();
                if (!baselines!.TryGetValue(Key(target, capability.Id), out var baseline)) return Task.FromResult(false);
                var io = hardware ??= open();
                if (VoltagePlane(capability.Id) is { } plane)
                {
                    var currentDomain = IntelOcMailbox.Read(io, plane);
                    var restoredDomain = (currentDomain & 0x1fffffU) | ((uint)baseline & ~0x1fffffU);
                    IntelOcMailbox.Write(io, plane, restoredDomain);
                    var restored = IntelOcMailbox.Read(io, plane) == restoredDomain;
                    if (restored) RemoveBaseline(target, capability.Id);
                    return Task.FromResult(restored);
                }
                if (!IsRapl(capability.Id)) return Task.FromResult(false);
                var current = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((current & (1UL << 63)) != 0) return Task.FromResult(false);
                var mask = IntelRaplEncoding.FieldMask(IsWindow(capability.Id), ShortTerm(capability.Id));
                io.WriteMsr(IntelRaplEncoding.LimitRegister, (current & ~mask) | (baseline & mask));
                var verified = (io.ReadMsr(IntelRaplEncoding.LimitRegister) & mask) == (baseline & mask);
                if (verified) RemoveBaseline(target, capability.Id);
                return Task.FromResult(verified);
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult(false); }
        }
    }
    private static string Key(IntelCpuControlTarget target, string capability) => $"{target.Id}\n{capability}";
    private void LoadBaselines()
    {
        if (baselines is not null) return;
        var stored = File.Exists(baselinePath)
            ? JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(baselinePath)) ?? [] : [];
        var normalized = new Dictionary<string, ulong>();
        var changed = false;
        foreach (var (key, value) in stored)
        {
            var first = key.IndexOf('\n');
            var last = key.LastIndexOf('\n');
            if (first <= 0 || last == key.Length - 1)
                throw new InvalidDataException(Describe(IntelCpuControlMessage.BaselineUnavailable));
            var canonical = first == last ? key : key[..first] + key[last..];
            if (normalized.TryGetValue(canonical, out var other) && other != value)
                throw new InvalidDataException(Describe(IntelCpuControlMessage.BaselineUnavailable));
            normalized[canonical] = value;
            changed |= canonical != key;
        }
        if (changed) SaveBaselines(normalized);
        else baselines = normalized;
    }
    private void CaptureBaseline(IntelCpuControlTarget target, string capability, ulong value)
    {
        LoadBaselines();
        var key = Key(target, capability);
        if (baselines!.ContainsKey(key)) return;
        var next = new Dictionary<string, ulong>(baselines) { [key] = value };
        SaveBaselines(next);
    }
    private void RemoveBaseline(IntelCpuControlTarget target, string capability)
    {
        var next = new Dictionary<string, ulong>(baselines!);
        next.Remove(Key(target, capability));
        SaveBaselines(next);
    }
    private void SaveBaselines(Dictionary<string, ulong> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        var temporary = baselinePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, baselinePath, overwrite: true);
        baselines = next;
    }
    private static bool Unavailable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException;
    private static IntelCpuControlOutcome Result(IntelCpuControlTarget target, IntelCpuControlCapability capability, string status, string? message) => new(target.Id, capability.Id, status, message);
    public void ResetDetection()
    {
        lock (gate)
        {
            CheckLifetime();
            try { hardware?.Dispose(); } finally { hardware = null; }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { hardware?.Dispose(); } finally { hardware = null; }
        }
    }
}

using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers.Intel;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Intel CPU control through the signed PawnIO module. Probing submits read queries only.</summary>
public sealed class IntelCpuControlWriter : IControlWriter, IControlDetectionCache, IDisposable
{
    private readonly Func<IIntelCpuHardware> open;
    private readonly string baselinePath;
    private readonly object gate = new();
    private IIntelCpuHardware? hardware;
    private Dictionary<string, ulong>? baselines;

    public IntelCpuControlWriter(IHostEnvironment environment)
        : this(() => new PawnIoIntelCpuHardware(environment.ContentRootPath), Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "intel-cpu-baselines.json")) { }
    internal IntelCpuControlWriter(Func<IIntelCpuHardware> open, string baselinePath)
    { this.open = open; this.baselinePath = baselinePath; }

    private static bool IsMine(ControlObject target, string capability)
        => target.Kind == ControlObjectKinds.Cpu && target.Platform.Vendor == ControlVendors.Intel
            && target.Platform.OperatingSystem == ControlOperatingSystems.Windows && capability.StartsWith("cpu.", StringComparison.Ordinal);
    public string? ChannelOf(ControlObject target, string capabilityId) => IsMine(target, capabilityId) ? ControlChannels.IntelMsr : null;
    private static bool IsRapl(string id) => id is "cpu.power-limit" or "cpu.short-power-limit" or "cpu.power-limit-window" or "cpu.short-power-limit-window";
    private static bool ShortTerm(string id) => id.StartsWith("cpu.short-", StringComparison.Ordinal);
    private static bool IsWindow(string id) => id.EndsWith("-window", StringComparison.Ordinal);

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        if (!IsMine(target, capability.Id)) return ControlWriteAvailability.NotMine;
        if (!IsRapl(capability.Id) && IntelOcMailbox.Plane(capability.Id) is null) return ControlWriteAvailability.No(
            capability.Id == "cpu.power-limit-mmio"
                ? "IntelMCHBAR 官方签名模块只开放读取；MMIO 写入尚未具备可部署的签名模块。"
                : "IntelMSR 官方签名模块未开放这一寄存器；该项写入尚未落实。",
            kind: ControlUnavailableKinds.NotImplemented);
        lock (gate)
        {
            try
            {
                var io = hardware ??= open();
                if (IntelOcMailbox.Plane(capability.Id) is { } plane)
                {
                    if (!io.CanWriteMsr(IntelOcMailbox.Register)) return ControlWriteAvailability.No("当前签名模块未开放 OC 邮箱。", kind: ControlUnavailableKinds.Component);
                    IntelOcMailbox.Read(io, plane);
                    return ControlWriteAvailability.Yes(capability.Range);
                }
                var limits = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((limits & (1UL << 63)) != 0) return ControlWriteAvailability.No("固件锁定了 Intel 功耗上限寄存器。", canRead: true);
                if (!io.CanWriteMsr(IntelRaplEncoding.LimitRegister)) return ControlWriteAvailability.No("当前模块未开放功耗上限写入。", kind: ControlUnavailableKinds.Component, canRead: true);
                var units = io.ReadMsr(IntelRaplEncoding.UnitsRegister);
                var maximum = IsWindow(capability.Id) ? IntelRaplEncoding.DecodeWindow(127, units) : 0x7fff * IntelRaplEncoding.PowerUnit(units);
                var declared = capability.Range;
                return ControlWriteAvailability.Yes(declared is null ? null : declared with { Maximum = Math.Min(declared.Maximum, maximum) });
            }
            catch (Exception error) when (Unavailable(error))
            { return ControlWriteAvailability.No(error.Message,
                kind: error is FileNotFoundException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                    ? ControlUnavailableKinds.Component : ControlUnavailableKinds.Platform); }
        }
    }

    public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            try
            {
                var availability = Probe(target, capability);
                if (!availability.CanWrite) return Task.FromResult(Result(target, capability, ControlApplyStatuses.Unsupported, availability.Reason));
                if (setting.Number is not { } number || !double.IsFinite(number)) return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, "需要有效数值。"));
                if (availability.Range is { } range && (number < range.Minimum || number > range.Maximum)) return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, "数值超出当前硬件范围。"));
                var io = hardware!;
                if (IntelOcMailbox.Plane(capability.Id) is { } plane)
                {
                    var before = IntelOcMailbox.Read(io, plane);
                    // Change the offset field only; preserve the domain's voltage target/mode.
                    var encoded = (before & 0x1fffffU) | IntelOcMailbox.Encode(number);
                    CaptureBaseline(target, capability.Id, before);
                    IntelOcMailbox.Write(io, plane, encoded);
                    var after = IntelOcMailbox.Read(io, plane);
                    return Task.FromResult(Result(target, capability, after == encoded ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed,
                        after == encoded ? $"已写入并回读 {IntelOcMailbox.Decode(after):G6} mV。" : "电压回读不一致；固件可能锁定或联动了电压域。"));
                }
                var units = io.ReadMsr(IntelRaplEncoding.UnitsRegister);
                var original = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((original & (1UL << 63)) != 0) return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, "固件已锁定功耗寄存器。"));
                var shortTerm = ShortTerm(capability.Id);
                var window = IsWindow(capability.Id);
                var mask = IntelRaplEncoding.FieldMask(window, shortTerm);
                CaptureBaseline(target, capability.Id, original & mask);
                var next = window ? IntelRaplEncoding.SetWindow(original, units, shortTerm, number) : IntelRaplEncoding.SetPower(original, units, shortTerm, number);
                io.WriteMsr(IntelRaplEncoding.LimitRegister, next);
                var readback = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                if ((readback & mask) != (next & mask)) return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, "写入已返回，但回读不一致；固件可能拒绝或覆盖了设置。"));
                var actual = window ? IntelRaplEncoding.ReadWindow(readback, units, shortTerm) : IntelRaplEncoding.ReadPower(readback, units, shortTerm);
                return Task.FromResult(Result(target, capability, ControlApplyStatuses.Applied, $"已写入并回读；实际硬件档位 {actual:G6} {capability.Range?.Unit}。"));
            }
            catch (Exception error) when (Unavailable(error) || error is ArgumentOutOfRangeException)
            { return Task.FromResult(Result(target, capability, ControlApplyStatuses.Failed, error.Message)); }
        }
    }

    public Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return Task.FromResult<ControlActualValue?>(null);
        if (!IsRapl(capability.Id) && IntelOcMailbox.Plane(capability.Id) is null) return Task.FromResult<ControlActualValue?>(null);
        lock (gate)
        {
            try
            {
                var io = hardware ??= open();
                if (IntelOcMailbox.Plane(capability.Id) is { } plane)
                    return Task.FromResult<ControlActualValue?>(new(target.Id, capability.Id, Number: IntelOcMailbox.Decode(IntelOcMailbox.Read(io, plane)), Unit: ControlUnits.Millivolt));
                var units = io.ReadMsr(IntelRaplEncoding.UnitsRegister);
                var limits = io.ReadMsr(IntelRaplEncoding.LimitRegister);
                var value = IsWindow(capability.Id) ? IntelRaplEncoding.ReadWindow(limits, units, ShortTerm(capability.Id)) : IntelRaplEncoding.ReadPower(limits, units, ShortTerm(capability.Id));
                return Task.FromResult<ControlActualValue?>(new(target.Id, capability.Id, Number: value, Unit: capability.Range?.Unit));
            }
            catch (Exception error) when (Unavailable(error)) { return Task.FromResult<ControlActualValue?>(null); }
        }
    }

    public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> stillConfigured, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return Task.FromResult(false);
        lock (gate)
        {
            try
            {
                LoadBaselines();
                if (!baselines!.TryGetValue(Key(target, capability.Id), out var baseline)) return Task.FromResult(false);
                var io = hardware ??= open();
                if (IntelOcMailbox.Plane(capability.Id) is { } plane)
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
    private static string Key(ControlObject target, string capability) => $"{target.Id}\n{target.DisplayName}\n{capability}";
    private void LoadBaselines() => baselines ??= File.Exists(baselinePath)
        ? JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(baselinePath)) ?? [] : [];
    private void CaptureBaseline(ControlObject target, string capability, ulong value)
    {
        LoadBaselines();
        var key = Key(target, capability);
        if (baselines!.ContainsKey(key)) return;
        var next = new Dictionary<string, ulong>(baselines) { [key] = value };
        SaveBaselines(next);
    }
    private void RemoveBaseline(ControlObject target, string capability)
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
    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException;
    private static ControlApplyOutcome Result(ControlObject target, ControlCapability capability, string status, string? message) => new(target.Id, capability.Id, status, message);
    public void ResetDetection() { lock (gate) { hardware?.Dispose(); hardware = null; } }
    public void Dispose() => ResetDetection();
}

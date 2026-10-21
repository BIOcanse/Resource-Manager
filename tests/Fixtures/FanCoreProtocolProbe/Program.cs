using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;

// No default discovery: this process can reach only an injected in-memory backend.
// The protocol, temperature storage, engine and EOF cleanup are production code.
using var guard = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
var deferredOpens = int.TryParse(Environment.GetEnvironmentVariable("RM_FAN_CORE_PROBE_DEFER_OPEN"), out var failures) ? failures : 0;
var restoreAllCheck = args.Length == 1 && args[0] == "--restore-all-check";
var stopAllCheck = args.Length == 1 && args[0] == "--engine-stop-all-check";
var releaseFault = Environment.GetEnvironmentVariable("RM_FAN_CORE_PROBE_FAIL_RESTORE") == "1";
var faultBackend = restoreAllCheck || stopAllCheck || releaseFault ? new FaultFan(releaseFault) : null;
IFanBackend backend = faultBackend ?? (IFanBackend)new MemoryFan(deferredOpens);
var core = new FanCore(backend);
try
{
    core.Open(); // Like the product entrypoint, continue serving after failed discovery.
    if (restoreAllCheck)
    {
        if (!core.TrySetDuty(0, 40, out _) || !core.TrySetDuty(1, 50, out _)) return 3;
        core.RestoreAll();
        if (faultBackend!.IsAutomatic(0) || !faultBackend.IsAutomatic(1)) return 4;
        core.RestoreAll();
        return faultBackend.Automatic && faultBackend.FirstFanRestores == 2 ? 0 : 5;
    }
    if (stopAllCheck)
    {
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromHours(1));
        var curve = new FanCurvePoint[] { new(60, 40), new(95, 100) };
        if (!backend.TrySetDuty(0, 40, out _) || !backend.TrySetDuty(1, 50, out _)) return 6;
        if (!engine.TryStart(new(0, curve, [new("cpu", () => 60)]), out _)
            || !engine.TryStart(new(1, curve, [new("cpu", () => 60)]), out _)) return 6;
        engine.StopAll();
        if (!engine.IsRunning(0) || engine.ActiveCurve(0) is not null || engine.IsRunning(1)
            || faultBackend!.IsAutomatic(0) || !faultBackend.IsAutomatic(1)) return 7;
        engine.StopAll();
        return !engine.IsRunning(0) && faultBackend.Automatic && faultBackend.FirstFanRestores == 2 ? 0 : 8;
    }
    FanCommandServer.Run(core, Console.In, Console.Out);
}
catch (Exception error)
{
    // Return the deliberate fixture fault without an unhandled process crash.
    Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
    return 10;
}
finally { core.Dispose(); }
if (faultBackend is not null)
    return faultBackend.Automatic && faultBackend.FirstFanDrives == 5 && faultBackend.FirstFanRestores >= 2 ? 0 : 9;
var memoryBackend = (MemoryFan)backend;
return memoryBackend.Automatic && (deferredOpens == 0 || memoryBackend.Drives == 0) ? 0 : 2;

sealed class MemoryFan(int deferredOpens) : IFanBackend
{
    private readonly object gate = new();
    private bool automatic = true;
    private double duty;
    private int opens;
    public int Drives { get; private set; }
    public string Name => "owned-memory-fan";
    public bool Automatic { get { lock (gate) return automatic; } }
    public bool TryOpen(out string? reason)
    {
        if (++opens <= deferredOpens)
        {
            reason = $"Owned deferred fixture attempt {opens} is not ready.";
            return false;
        }
        reason = null;
        return true;
    }
    public IReadOnlyList<FanDescription> Describe()
        => [new(0, "Owned memory fan", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true)];
    public IReadOnlyList<FanState> Read()
    {
        lock (gate) return [new(0, "Owned memory fan", null, automatic ? null : duty, null, true, true, automatic)];
    }
    public bool TrySetDuty(int fan, double percent, out string? reason)
    {
        reason = null;
        lock (gate) { automatic = false; duty = percent; Drives++; }
        return true;
    }
    public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
    public bool TrySetAutomatic(int fan, out string? reason)
    {
        reason = null;
        lock (gate) automatic = true;
        return true;
    }
    public void Dispose() { }
}

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

internal sealed class DelegateFanSession(Func<JsonElement, JsonElement> handle, Action? dispose = null) : IFanControlCoreSession
{
    private readonly HashSet<int> plans = [];
    public bool SoftwareCurveRunNeeded { get; private set; }
    public event Action<bool>? SoftwareCurveRunNeededChanged;
    public JsonElement Handle(JsonElement request)
    {
        var response = handle(request);
        if (request.TryGetProperty("fan", out var fan) && response.TryGetProperty("ok", out var ok)
            && ok.ValueKind == JsonValueKind.True)
        {
            var op = request.GetProperty("op").GetString();
            if (op == "setcurve")
            {
                if (response.TryGetProperty("softwarePlanRecorded", out var recorded) && recorded.ValueKind == JsonValueKind.True)
                    plans.Add(fan.GetInt32());
                else if (FanControlCoreClient.ParseCurveReceipt(response) is not null)
                {
                    if (response.TryGetProperty("execution", out var execution) && execution.GetString() == "software")
                        plans.Add(fan.GetInt32());
                    else plans.Remove(fan.GetInt32());
                }
            }
            else if (op is "set" or "boost" or "auto") plans.Remove(fan.GetInt32());
            var needed = plans.Count != 0;
            if (SoftwareCurveRunNeeded != needed)
            {
                SoftwareCurveRunNeeded = needed;
                SoftwareCurveRunNeededChanged?.Invoke(needed);
            }
        }
        return response;
    }
    public Task RunSoftwareCurveAsync(CancellationToken stop) => Task.Delay(Timeout.Infinite, stop);
    public void Dispose() => dispose?.Invoke();
}

internal sealed class FanTestEnvironment(string root = @"C:\FanControlFake") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class LibraryFanSession : IFanControlCoreSession
{
    private readonly FanControlCore.FanControlSession session;
    internal LibraryFanSession(FanControlCore.Discovery.FanCore core) => session = new(core);
    public JsonElement Handle(JsonElement request) => session.Handle(request);
    public bool SoftwareCurveRunNeeded => session.SoftwareCurveRunNeeded;
    public event Action<bool>? SoftwareCurveRunNeededChanged
    {
        add => session.SoftwareCurveRunNeededChanged += value;
        remove => session.SoftwareCurveRunNeededChanged -= value;
    }
    public Task RunSoftwareCurveAsync(CancellationToken stop) => session.RunSoftwareCurveAsync(stop);
    public void SetDiagnosticSink(Action<string> sink) => session.SetDiagnosticSink(sink);
    public string? InFlightOperation => session.InFlightOperation;
    public void Dispose() => session.Dispose();
}

/// <summary>Entirely managed fake hardware used by the actual packaged core and engine.</summary>
internal sealed class ProbeFanBackend(int deferredOpens = 0, bool restoreFault = false, bool twoFans = false)
    : FanControlCore.IFanBackend
{
    private readonly object sync = new();
    private readonly double?[] duties = new double?[restoreFault || twoFans ? 2 : 1];
    private int opens, restorationAttempts;
    public int RestoreFailures { get; set; }
    public bool ThrowDrive { get; set; }
    public int DriveFailures { get; set; }
    public bool ThrowFirstRelease { get; set; }
    public bool Disposed { get; private set; }
    public string Name => "rm-in-memory-core";
    public bool TryOpen(out string? reason)
    {
        var attempt = Interlocked.Increment(ref opens);
        reason = attempt <= deferredOpens ? "Deferred fake discovery " + attempt : null;
        return reason is null;
    }
    public IReadOnlyList<FanControlCore.FanDescription> Describe()
        => Enumerable.Range(0, duties.Length).Select(index => new FanControlCore.FanDescription(index,
            "Fixture " + index, true, true, FanControlCore.FanRoles.Cpu, SupportsSoftwareCurve: true)).ToArray();
    public IReadOnlyList<FanControlCore.FanState> Read()
    {
        lock (sync) return Enumerable.Range(0, duties.Length).Select(index =>
            new FanControlCore.FanState(index, "Fixture " + index, 2000, duties[index], null, true, true,
                duties[index] is null)).ToArray();
    }
    public bool TrySetDuty(int fan, double percent, out string? reason)
    { lock (sync) duties[fan] = percent; reason = null; return true; }
    public bool TryDriveDuty(int fan, double percent, out string? reason)
    {
        if (ThrowDrive) throw new ApplicationException("injected background curve tick");
        if (DriveFailures-- > 0) { reason = "Injected drive failure"; return false; }
        if (restoreFault && fan == 0) { lock (sync) duties[fan] = percent; reason = "Injected drive failure"; return false; }
        return TrySetDuty(fan, percent, out reason);
    }
    public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
    public bool TrySetAutomatic(int fan, out string? reason)
    {
        if (fan == 0 && ThrowFirstRelease) { ThrowFirstRelease = false; throw new InvalidOperationException("Injected restoration exception"); }
        if (fan == 0 && (restoreFault && Interlocked.Increment(ref restorationAttempts) == 1))
            throw new InvalidOperationException("Injected restoration exception");
        if (fan == 0 && RestoreFailures-- > 0) { reason = "Injected restoration failure"; return false; }
        lock (sync) duties[fan] = null;
        reason = null;
        return true;
    }
    public void Dispose() => Disposed = true;
}

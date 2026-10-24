using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

internal sealed class DelegateFanSession(Func<JsonElement, JsonElement> handle, Action? dispose = null) : IFanControlCoreSession
{
    public JsonElement Handle(JsonElement request) => handle(request);
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

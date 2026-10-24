using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;

static class ProbeApplication
{
    public static void Main(string[] args)
    {
        if (args is ["--stdio"]) { Stdio(); return; }
#if !LEGACY_CORE
        if (args is ["--crash", var state]) { Crash(state); return; }
        if (args is ["--measure-embedded"]) { MeasureEmbedded(); return; }
        if (args is ["--measure-stdio", var executable]) { MeasureStdio(executable); return; }
#endif
        throw new ArgumentException("Expected an explicit fake-hardware probe mode");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Stdio()
    {
        using var core = new FanCore(new ProbeBackend(null));
        core.Open();
        FanCommandServer.Run(core, Console.In, Console.Out);
    }
#if !LEGACY_CORE
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Crash(string state)
    {
        var core = new FanCore(new ProbeBackend(state), new FanCoreOptions(state, MachineIdentity: () => "fixture-machine"));
        core.Open();
        if (!core.TrySetDuty(0, 40, out var reason)) throw new Exception(reason);
        Environment.Exit(0);
    }

    private static void WarmBaseline()
    {
        _ = JsonSerializer.Serialize(new { ok = true });
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MeasureEmbedded()
    {
        WarmBaseline();
        var before = PrivateBytes();
        var heapBefore = GC.GetTotalMemory(false);
        var watch = Stopwatch.StartNew();
        using var session = CreateAndDescribe();
        watch.Stop();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var after = PrivateBytes();
        Console.WriteLine(JsonSerializer.Serialize(new { beforePrivateBytes = before, afterPrivateBytes = after,
            addedPrivateBytes = after - before, managedHeapAdded = GC.GetTotalMemory(false) - heapBefore,
            firstDescribeMs = watch.Elapsed.TotalMilliseconds }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable CreateAndDescribe()
    {
        var session = new FanControlSession(new FanCore(new ProbeBackend(null)));
        var reply = session.Handle(JsonSerializer.SerializeToElement(new { id = 1, op = "describe" }));
        if (!reply.GetProperty("ok").GetBoolean()) { session.Dispose(); throw new Exception("describe failed"); }
        return session;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MeasureStdio(string executable)
    {
        WarmBaseline();
        var before = PrivateBytes();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--stdio");
        var watch = Stopwatch.StartNew();
        using var child = Process.Start(start)!;
        child.StandardInput.WriteLine("{\"id\":1,\"op\":\"describe\"}");
        child.StandardInput.Flush();
        var response = child.StandardOutput.ReadLine();
        watch.Stop();
        using var reply = JsonDocument.Parse(response!);
        if (!reply.RootElement.GetProperty("ok").GetBoolean()) throw new Exception("stdio describe failed");
        child.Refresh();
        var removed = child.PrivateMemorySize64;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var hostAdded = PrivateBytes() - before;
        child.StandardInput.Close();
        if (!child.WaitForExit(10000)) { child.Kill(); child.WaitForExit(); throw new Exception("stdio fixture timed out"); }
        if (child.ExitCode != 0) throw new Exception(child.StandardError.ReadToEnd());
        Console.WriteLine(JsonSerializer.Serialize(new { childPrivateBytes = removed, parentTransportAddedBytes = hostAdded,
            firstDescribeMs = watch.Elapsed.TotalMilliseconds }));
    }
    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }
#endif
}
sealed class ProbeBackend(string? stateDirectory) : IFanBackend
{
    public string Name => "rm-durable-probe";
    public bool TryOpen(out string? reason) { reason = null; return true; }
    public IReadOnlyList<FanDescription> Describe() => [new(0, "CPU fan", true, true, "cpu")];
    public IReadOnlyList<FanState> Read() => [new(0, "CPU fan", 2000, null, null, true, true, true)];
    private void Record(string operation)
    {
        if (stateDirectory is null) return;
        Directory.CreateDirectory(stateDirectory);
        File.AppendAllLines(Path.Combine(stateDirectory, "operations"), [operation + ":0"]);
    }
    public bool TrySetDuty(int fan, double percent, out string? reason) { Record("set"); reason = null; return true; }
    public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
    public bool TrySetAutomatic(int fan, out string? reason) { Record("auto"); reason = null; return true; }
    public void Dispose() { }
}

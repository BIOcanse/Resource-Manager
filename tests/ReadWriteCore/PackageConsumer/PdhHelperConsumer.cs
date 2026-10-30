using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Pdh;

internal static class PdhHelperConsumer
{
    internal static void RunPdh()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Require(new[] { typeof(NativePdhSession), typeof(NativePdhApi), typeof(NativePdhSnapshot),
            typeof(NativePdhGpuUsage), typeof(PdhSystemIoReader), typeof(PdhCpuPerformanceReader),
            typeof(HardwareBridgeProcessClient), typeof(HelperErrorOutput) }.All(type => type.Assembly == owner),
            "PDH and helper implementations are owned by the independent package");
        var native = new NativePdhApi();
        Require(native.GetAbiVersion() == NativeCoreAbi.Version, "packaged PDH ABI through shared resolver");
        var config = new NativePdhConfig { AbiVersion = NativeCoreAbi.Version,
            StructSize = (uint)Marshal.SizeOf<NativePdhConfig>(), BaselineResetIntervalMilliseconds = 5000 };
        Require(native.Create(config, out var handle) == NativePdhResultCode.Ok && handle != IntPtr.Zero,
            "packaged native PDH creates independently of the host");
        try
        {
            var header = new NativePdhFrameHeader { AbiVersion = NativeCoreAbi.Version,
                StructSize = (uint)Marshal.SizeOf<NativePdhFrameHeader>() };
            Require(native.Sample(handle, ref header) is NativePdhResultCode.Ok or NativePdhResultCode.NoData,
                "actual read-only PDH baseline");
        }
        finally { native.Destroy(handle); }

        var api = new RecordedPdh(); var clock = new Clock();
        var session = new NativePdhSession(api, new(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 1234), clock);
        Require(api.Creates == 0, "construction performs no native access");
        var read = session.Read(11);
        Require(api.Creates == 1 && api.Baseline == 1234 && api.Refreshes == 1 && api.Samples == 1,
            "explicit policy and topology reach the original collector");
        Require(read.Snapshot?.DiskObservation.Availability == NativePdhProviderAvailability.Available,
            "original per-domain availability");
        var io = new PdhSystemIoReader(session).Read(new(true, false));
        Require(io.Disk.ReadBytesPerSecond == 4096 && io.Disk.ActivePercent == 100
            && io.NetworkAvailability == NativePdhProviderAvailability.NotRequested && api.Samples == 1,
            "original IO units, clamping and one frame reuse");
        Require(io.Disk.HasValue(PdhDiskMetricValidity.ReadBytesPerSecond)
            && io.Disk.HasValue(PdhDiskMetricValidity.ActivePercent)
            && !io.Disk.HasValue(PdhDiskMetricValidity.WriteBytesPerSecond)
            && !io.Disk.HasValue(PdhDiskMetricValidity.QueueLength),
            "individual missing disk counters remain distinct from available readings");
        Require(NativePdhGpuUsage.BusiestEngines(read.Snapshot!.EngineRows)[1].Value == 70,
            "process rows combine per physical GPU engine and use busiest engine");
        Require(NativePdhGpuUsage.ByProcess(read.Snapshot.EngineRows)[(1, 7)] == 40,
            "per-process utilization does not sum different engines");
        api.Fail = true; clock.Ticks += 100;
        Require(session.Read().Availability == NativePdhProviderAvailability.LastGood,
            "original fallback keeps observation status explicit");
        clock.Ticks += 2000;
        Require(session.Read().Availability == NativePdhProviderAvailability.Unavailable,
            "caller lifetime expires the retained observation");
        session.Dispose(); session.Dispose();
        session.Read();
        Require(api.Destroys == 1 && api.Creates == 1 && api.Samples == 3, "released native handle never reopens");
        Console.WriteLine("ReadWriteCore PDH consumer passed: packaged native ABI/baseline, original domain status and units, physical-engine aggregation, explicit policy/clock, frame reuse, expiry and release.");
    }

    internal static async Task RunHelperAsync()
    {
        var stderr = new HelperErrorOutputOptions(32, 64, 96, "Microsoft.NETCore.App", "10.0.", "runtime-missing", "startup-failed");
        var errors = new HelperErrorOutput(new StringReader("Microsoft.NETCore.App 10.0.0\n" + new string('x', 50000)), stderr);
        await errors.Completion;
        Require(errors.Detail.Length <= 161 && errors.FailureReason == "runtime-missing", "caller bounded diagnostics and copy");
        Process? observer = null; var starts = 0;
        var script = "while ($null -ne ($line=[Console]::In.ReadLine())) { $r=$line | ConvertFrom-Json; [Console]::Out.WriteLine((@{id=$r.id; ok=$true; payload=$r.payload} | ConvertTo-Json -Compress)); [Console]::Out.Flush() }";
        Process? Start(ProcessStartInfo requested)
        {
            starts++;
            Require(requested.CreateNoWindow && !requested.UseShellExecute && requested.RedirectStandardError,
                "helper transport starts without a visible window");
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var process = Process.Start(start)!;
            observer = Process.GetProcessById(process.Id); _ = observer.SafeHandle;
            return process;
        }
        using var client = new HardwareBridgeProcessClient(() => Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), stderr)
            { StopTimeout = TimeSpan.FromSeconds(2), ReadingInterval = TimeSpan.FromSeconds(1) }, startFactory: Start);
        try
        {
            Require(starts == 0 && client.IsInstalled && starts == 0, "construction and path query do not start helpers");
            var reply = await client.SendAsync("recorded", new Dictionary<string, object?> { ["payload"] = "exact" }, default);
            Require(reply?.GetProperty("ok").GetBoolean() == true && reply?.GetProperty("payload").GetString() == "exact",
                "real helper pipe JSON reply through public API");
            client.Dispose();
            await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(observer.ExitCode == 0 && starts == 1, "input EOF releases exactly one helper without killing it");
            try { await client.SendAsync("recorded", null, default); throw new InvalidOperationException("Disposed helper reopened"); }
            catch (ObjectDisposedException) { }
        }
        finally { client.Dispose(); observer?.Dispose(); }
        Console.WriteLine("ReadWriteCore helper consumer passed: explicit path/timeouts/copy, bounded stderr, no constructor start, actual JSON pipe reply and EOF release; no hardware helper or driver writes.");
    }

    private static void Require(bool value, string description)
    { if (!value) throw new InvalidOperationException(description); }
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Ticks);
    }
    private sealed class RecordedPdh : INativePdhApi
    {
        internal int Creates, Refreshes, Samples, Destroys;
        internal uint Baseline;
        internal bool Fail;
        public uint GetAbiVersion() => NativeCoreAbi.Version;
        public NativePdhResultCode Create(in NativePdhConfig config, out IntPtr handle)
        { Creates++; Baseline = config.BaselineResetIntervalMilliseconds; handle = (IntPtr)1; return NativePdhResultCode.Ok; }
        public void Destroy(IntPtr handle) => Destroys++;
        public NativePdhResultCode RequestTopologyRefresh(IntPtr handle) { Refreshes++; return NativePdhResultCode.Ok; }
        public NativePdhResultCode Sample(IntPtr handle, ref NativePdhFrameHeader header)
        {
            Samples++; header.Sequence = (ulong)Samples; header.EngineRowCount = 3;
            header.Flags = (uint)(NativePdhFrameFlags.HasData | NativePdhFrameFlags.AllDomainsComplete);
            return Fail ? NativePdhResultCode.PdhError : NativePdhResultCode.Ok;
        }
        public NativePdhResultCode CopyFrame(IntPtr handle, ulong sequence, NativePdhGpuEngineRow[] engines,
            NativePdhGpuMemoryRow[] memory, out NativePdhSystemIo io)
        {
            engines[0] = new() { AdapterLuid = 1, ProcessId = 7, PhysicalIndex = 0, EngineIndex = 0, UsagePercent = 40 };
            engines[1] = new() { AdapterLuid = 1, ProcessId = 8, PhysicalIndex = 0, EngineIndex = 0, UsagePercent = 30 };
            engines[2] = new() { AdapterLuid = 1, ProcessId = 7, PhysicalIndex = 0, EngineIndex = 1, UsagePercent = 20 };
            io = new() { StructSize = (uint)Marshal.SizeOf<NativePdhSystemIo>(),
                ValidMask = NativePdhIoValidMask.DiskActive | NativePdhIoValidMask.DiskRead,
                DiskActivePercent = 120, DiskReadBytesPerSecond = 4096 };
            return NativePdhResultCode.Ok;
        }
        public NativePdhResultCode GetDiagnostics(IntPtr handle, ref NativePdhDiagnostics diagnostics) => NativePdhResultCode.Ok;
    }
}

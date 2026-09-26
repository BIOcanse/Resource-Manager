using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;

namespace Resource_Manager_APP.Tests;

internal sealed class GpuPlacementMpvTestProcess : IAsyncDisposable
{
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint handle, out ulong birth, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool IsProcessInJob(nint process, nint job, out bool result);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    private sealed record ForegroundSnapshot(long WindowHandle, uint ProcessId, uint ThreadId);
    private readonly string root;
    private readonly Process child;
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource deadline;
    private readonly nint foreground;
    private readonly Task stdout;
    private readonly Task stderr;
    private readonly List<object> messages = [];
    private readonly List<object> phases = [];
    private readonly string[] arguments;
    private StreamReader? reader;
    private StreamWriter? writer;
    private int requestId;
    private bool stopped;
    internal GpuPlacementProcessInstance Identity { get; private set; } = null!;
    internal Func<string, Task>? AfterPhaseSnapshot { get; set; }

    internal GpuPlacementMpvTestProcess(string executable, string root, TimeSpan? timeout = null)
    {
        this.root = root;
        deadline = new(timeout ?? TimeSpan.FromSeconds(45));
        foreground = GetForegroundWindow();
        var name = "rm-mpv-first-use-" + Guid.NewGuid().ToString("N");
        pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        arguments = ["--no-config", "--load-scripts=no", "--ytdl=no", "--osc=no", "--no-audio",
            "--idle=yes", "--vo=gpu", "--gpu-api=opengl", "--gpu-context=win", "--hwdec=no", "--gpu-hwdec-interop=no",
            "--window-minimized=yes", "--force-window=yes", "--border=no", "--geometry=160x120+24+24",
            "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no", "--no-terminal",
            "--msg-level=all=v", "--log-file=" + Path.Combine(root, "mpv.log"), @"--input-ipc-server=\\.\pipe\" + name];
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        child = Process.Start(info) ?? throw new InvalidOperationException("mpv start");
        stdout = CopyLog(child.StandardOutput, Path.Combine(root, "mpv.stdout.log"));
        stderr = CopyLog(child.StandardError, Path.Combine(root, "mpv.stderr.log"));
    }

    internal async Task<string> ConnectAsync()
    {
        Assert.True(GetProcessTimes(child.Handle, out var birth, out _, out _, out _));
        Assert.True(IsProcessInJob(child.Handle, 0, out var inJob));
        Assert.True(inJob);
        Identity = new(child.Id, birth, Path.GetFileNameWithoutExtension(child.StartInfo.FileName), child.StartInfo.FileName);
        await Save("mpv-launch.json", new { Identity, arguments, inJob, foreground = (long)foreground });
        await pipe.ConnectAsync(10000, deadline.Token);
        reader = new(pipe, Encoding.UTF8, false, 4096, true);
        writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await Command("loadfile", "av://lavfi:testsrc2=size=160x120:rate=10");
        await Task.Delay(1500, deadline.Token);
        return (await Snapshot("original", "gpu"))!;
    }

    internal async Task<string> RecreateAsync(string stage)
    {
        await Command("set_property", "vo", "null");
        await Task.Delay(700, deadline.Token);
        await Snapshot(stage + "-released", "null");
        await Command("set_property", "vo", "gpu");
        await Task.Delay(1500, deadline.Token);
        return (await Snapshot(stage, "gpu"))!;
    }

    private async Task<string?> Snapshot(string stage, string expectedVo)
    {
        Assert.Equal(child.Id, (await Command("get_property", "pid")).GetInt32());
        Assert.True(GetProcessTimes(child.Handle, out var birth, out _, out _, out _));
        Assert.Equal(Identity.ProcessStartKey, birth);
        Assert.Equal(expectedVo, (await Command("get_property", "current-vo")).GetString());
        Assert.True((await Command("get_property", "vo-configured")).GetBoolean());
        var video = await Command("get_property", "video-out-params");
        Assert.Equal(160, video.GetProperty("w").GetInt32());
        Assert.Equal(120, video.GetProperty("h").GetInt32());
        var position = await Command("get_property", "time-pos");
        JsonElement? laterPosition = null;
        string? renderer = null;
        string? screenshot = null;
        if (expectedVo == "gpu")
        {
            Assert.Equal("win", (await Command("get_property", "current-gpu-context")).GetString());
            Assert.False((await Command("get_property", "pause")).GetBoolean());
            Assert.False((await Command("get_property", "eof-reached")).GetBoolean());
            await Task.Delay(350, deadline.Token);
            laterPosition = await Command("get_property", "time-pos");
            Assert.True(laterPosition.Value.GetDouble() > position.GetDouble());
            screenshot = Path.Combine(root, stage + ".png");
            await Command("screenshot-to-file", screenshot, "window");
            Assert.True(File.Exists(screenshot));
            Assert.InRange(new FileInfo(screenshot).Length, 64, 2 * 1024 * 1024);
        }
        string log;
        using (var stream = new FileStream(Path.Combine(root, "mpv.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var logReader = new StreamReader(stream))
        {
            Assert.InRange(stream.Length, 1, 8 * 1024 * 1024);
            log = await logReader.ReadToEndAsync(deadline.Token);
        }
        var renderers = log.Split('\n').Where(line => line.Contains(" GL_RENDERER='", StringComparison.Ordinal)).ToArray();
        if (expectedVo == "gpu")
        {
            Assert.NotEmpty(renderers);
            Assert.Equal(phases.Count / 2 + 1, renderers.Length);
            const string marker = "GL_RENDERER='";
            var last = renderers[^1];
            var start = last.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            renderer = last[start..last.LastIndexOf('\'')];
            Assert.NotEmpty(renderer);
            var currentOutputLog = log[log.LastIndexOf(last, StringComparison.Ordinal)..];
            Assert.Contains("VO: [gpu]", currentOutputLog, StringComparison.Ordinal);
            Assert.Contains("playback restart complete", currentOutputLog, StringComparison.Ordinal);
        }
        await File.WriteAllTextAsync(Path.Combine(root, stage + ".mpv.log"), log);
        child.Refresh();
        var foregroundSnapshot = ReadForeground();
        var modules = child.Modules.Cast<ProcessModule>().Select(module => new { module.ModuleName, module.FileName }).ToArray();
        phases.Add(new { stage, expectedVo, renderer, rendererCount = renderers.Length, video, position, laterPosition, screenshot, modules,
            qpc = Stopwatch.GetTimestamp(), pid = child.Id, birth, mainWindowHandle = (long)child.MainWindowHandle, foregroundSnapshot });
        await Save("mpv-phases.json", phases);
        AssertForegroundIsNotTarget(foregroundSnapshot);
        if (AfterPhaseSnapshot is not null) await AfterPhaseSnapshot(stage);
        return renderer;
    }

    private async Task<JsonElement> Command(params object[] command)
    {
        var id = ++requestId;
        var request = new { command, request_id = id };
        messages.Add(new { direction = "request", qpc = Stopwatch.GetTimestamp(), value = request });
        await writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
        for (var count = 0; count < 512; count++)
        {
            var line = await reader!.ReadLineAsync(deadline.Token) ?? throw new EndOfStreamException("mpv IPC");
            using var document = JsonDocument.Parse(line);
            var value = document.RootElement.Clone();
            messages.Add(new { direction = "reply", qpc = Stopwatch.GetTimestamp(), value });
            if (!value.TryGetProperty("request_id", out var replyId) || replyId.GetInt32() != id) continue;
            await Save("mpv-ipc.json", messages);
            Assert.Equal("success", value.GetProperty("error").GetString());
            return value.TryGetProperty("data", out var data) ? data.Clone() : default;
        }
        throw new InvalidDataException("mpv IPC message bound");
    }

    internal async Task StopAsync()
    {
        if (stopped) return;
        var forced = false;
        string? cleanupError = null;
        ForegroundSnapshot? foregroundSnapshot = null;
        try
        {
            if (!child.HasExited && writer is not null)
            {
                var request = new { command = new object[] { "quit", 0 }, request_id = ++requestId };
                messages.Add(new { direction = "request", qpc = Stopwatch.GetTimestamp(), value = request });
                await writer.WriteLineAsync(JsonSerializer.Serialize(request)).WaitAsync(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception error) { cleanupError = error.ToString(); }
        finally
        {
            if (!child.HasExited)
            {
                forced = true;
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            await Save("mpv-ipc.json", messages);
            foregroundSnapshot = ReadForeground();
            await Save("mpv-cleanup.json", new { Identity, forcedCleanup = forced, cleanupError,
                exitCode = child.HasExited ? (int?)child.ExitCode : null, foregroundBefore = (long)foreground,
                foregroundAfter = foregroundSnapshot.WindowHandle, foregroundSnapshot });
            stopped = true;
        }
        Assert.Null(cleanupError);
        Assert.False(forced);
        Assert.Equal(0, child.ExitCode);
        AssertForegroundIsNotTarget(foregroundSnapshot!);
        Assert.Empty(await File.ReadAllTextAsync(Path.Combine(root, "mpv.stderr.log")));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { writer?.Dispose(); reader?.Dispose(); await pipe.DisposeAsync(); child.Dispose(); deadline.Dispose(); }
    }

    private Task Save(string name, object value)
        => File.WriteAllTextAsync(Path.Combine(root, name), JsonSerializer.Serialize(value));

    private static ForegroundSnapshot ReadForeground()
    {
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out var process);
        return new((long)window, process, thread);
    }

    private void AssertForegroundIsNotTarget(ForegroundSnapshot snapshot)
    {
        if (snapshot.WindowHandle == 0) return;
        Assert.NotEqual(0U, snapshot.ThreadId);
        Assert.NotEqual(0U, snapshot.ProcessId);
        Assert.NotEqual((uint)child.Id, snapshot.ProcessId);
    }

    private static async Task CopyLog(StreamReader source, string path)
    {
        await using var output = new StreamWriter(path, false, new UTF8Encoding(false));
        var buffer = new char[4096];
        var total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer);
            if (count == 0) return;
            total = checked(total + count);
            if (total > 2 * 1024 * 1024) throw new IOException("mpv log bound");
            await output.WriteAsync(buffer.AsMemory(0, count));
            await output.FlushAsync();
        }
    }
}

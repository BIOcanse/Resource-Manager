using System.Diagnostics;
using System.Text;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Windows.Gpu.Control;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeSessionTests
{
    [Fact]
    public async Task CancellationDuringInternalDescribeDoesNotStartRequestedWriteOrDiscardCompletedFacts()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"rm-session-cancel-"+Guid.NewGuid().ToString("N"))).FullName;
        var started = Path.Combine(directory,"describe-started");
        var release = Path.Combine(directory,"release");
        var setter = Path.Combine(directory,"setter");
        var executable = Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        var launches = 0;
        Process? observer = null;
        Process? Start(ProcessStartInfo ignored)
        {
            launches++;
            var script = $$"""
                $ErrorActionPreference='Stop'
                $description=[Console]::In.ReadLine() | ConvertFrom-Json
                if ($description.op -ne 'describe') { throw 'Expected initial describe' }
                [IO.File]::WriteAllText('{{started}}','describe accepted')
                $deadline=[DateTime]::UtcNow.AddSeconds(20)
                while (-not [IO.File]::Exists('{{release}}') -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(10) }
                [Console]::Out.WriteLine('{"available":true}')
                [Console]::Out.Flush()
                $requests=0
                while ($null -ne ($line=[Console]::In.ReadLine()) -and $requests -lt 10) {
                    $request=$line | ConvertFrom-Json
                    $requests++
                    if ($request.op -like 'set-*') { [IO.File]::WriteAllText('{{setter}}','unexpected setter') }
                    [Console]::Out.WriteLine((@{op=$request.op;requests=$requests} | ConvertTo-Json -Compress))
                    [Console]::Out.Flush()
                }
                """;
            var info = new ProcessStartInfo(executable)
            { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach (var argument in new[] {"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})
                info.ArgumentList.Add(argument);
            var process=Process.Start(info)!;
            observer=Process.GetProcessById(process.Id);
            _=observer.SafeHandle;
            return process;
        }
        using var bridge = new HardwareBridgeProcessClient(() => executable,
            new(TimeSpan.FromSeconds(20),TimeSpan.FromSeconds(1),new(128,1024,1024,".NET","10","missing","failed")),startFactory:Start);
        using var cancellation = new CancellationTokenSource();
        Task<System.Text.Json.JsonElement?>? pending = null;
        try
        {
            pending=bridge.SendAsync("set-owned-test",null,cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(started)) await Task.Delay(10,deadline.Token);
            cancellation.Cancel();
            File.WriteAllText(release,"release");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.False(File.Exists(setter));
            var status=(await bridge.SendAsync("status",null,default))!.Value;
            Assert.Equal("status",status.GetProperty("op").GetString());
            Assert.Equal(1,status.GetProperty("requests").GetInt32());
            Assert.Equal(1,launches);
        }
        finally
        {
            File.WriteAllText(release,"release");
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            bridge.Dispose();
            if (observer is not null)
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observer.Dispose();
            }
            foreach (var path in new[] {started,release,setter}) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task SharedOwnersDescribeOnceAndFirstReadAfterExitDescribesNewSession()
    {
        var observers = new List<Process>();
        var launches = 0;
        var executable = Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        Process? Start(ProcessStartInfo ignored)
        {
            var session = ++launches;
            var script = $$"""
                $ErrorActionPreference='Stop'
                $requests=0; $describes=0; $reads=0
                while ($null -ne ($line=[Console]::In.ReadLine()) -and $requests -lt 100) {
                    $request=$line | ConvertFrom-Json
                    $requests++
                    if ($request.op -eq 'describe') { $describes++ }
                    if ($request.op -eq 'read') { $reads++ }
                    [Console]::Out.WriteLine((@{ok=$true;available=$true;powerTable=$true;integratedGpuCurveOptimizer=$true;requests=$requests;describes=$describes;reads=$reads;session={{session}};stapmLimitWatts={{40+session}};integratedGpuCurveOptimizerCounts={{session}}} | ConvertTo-Json -Compress))
                    [Console]::Out.Flush()
                    if ($request.op -eq 'finish') { break }
                }
                """;
            var info = new ProcessStartInfo(executable)
            { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach (var argument in new[] {"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})
                info.ArgumentList.Add(argument);
            var process = Process.Start(info)!;
            var observer = Process.GetProcessById(process.Id);
            _ = observer.SafeHandle;
            observers.Add(observer);
            return process;
        }
        using var bridge = new HardwareBridgeProcessClient(() => executable,
            new(TimeSpan.FromSeconds(20),TimeSpan.FromSeconds(1),new(128,1024,1024,".NET","10","missing","failed")),startFactory:Start);
        // Probe never creates either baseline path.
        var directory = Path.Combine(Path.GetTempPath(),"rm-session-"+Guid.NewGuid().ToString("N"));
        using var cpu = new AmdCpuControlResource(bridge,AmdCpuControlOptions.InDirectory(directory));
        using var gpu = new IntegratedGpuControlResource(bridge,new RecordedIntegratedIntelHardware(),new(Path.Combine(directory,"igpu.json"),_=>Task.FromResult(false)));
        var cpuTarget = new AmdCpuControlTarget("cpu:0",true);
        var power = new AmdCpuControlCapability(AmdCpuControlField.PowerLimit);
        var gpuTarget = new IntegratedGpuControlTarget("gpu:0",IntegratedGpuControlRoute.AmdIntegrated);
        var co = new IntegratedGpuControlCapability(IntegratedGpuControlField.CurveOptimizer);
        try
        {
            // No Probe warm-up: the first read must establish the session facts itself.
            Assert.Equal(41,(await cpu.ReadAsync(cpuTarget,power))!.Number);
            for (var round=0;round<20;round++)
            {
                Assert.True(cpu.Probe(cpuTarget,power).CanWrite);
                Assert.True(gpu.Probe(gpuTarget,co).CanWrite);
            }
            Assert.Equal(1,(await gpu.ReadAsync(gpuTarget,co))!.Number);
            var status = (await bridge.SendAsync("finish",null,default))!.Value;
            Assert.Equal(4,status.GetProperty("requests").GetInt32());
            Assert.Equal(1,status.GetProperty("describes").GetInt32());
            Assert.Equal(2,status.GetProperty("reads").GetInt32());
            await observers[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2,(await gpu.ReadAsync(gpuTarget,co))!.Number);
            Assert.Equal(42,(await cpu.ReadAsync(cpuTarget,power))!.Number);
            status = (await bridge.SendAsync("describe",null,default))!.Value;
            Assert.Equal(2,status.GetProperty("session").GetInt32());
            Assert.Equal(1,status.GetProperty("requests").GetInt32());
            var second = (await bridge.SendAsync("status",null,default))!.Value;
            Assert.Equal(4,second.GetProperty("requests").GetInt32());
            Assert.Equal(1,second.GetProperty("describes").GetInt32());
            Assert.Equal(2,second.GetProperty("reads").GetInt32());
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            bridge.Dispose();
            foreach (var observer in observers)
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observer.Dispose();
            }
        }
    }
}

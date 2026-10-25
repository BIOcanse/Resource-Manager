using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class AmdIntegratedGpuBaselineTests
{
    private static readonly ControlCapability Capability = new("gpu.curve-optimizer", ControlValueKinds.Number, true,
        Range: new(-30, 10, 1, ControlUnits.Step));
    private static ControlObject Target(string id) => new(id, ControlObjectKinds.Gpu, "AMD iGPU",
        new(ControlOperatingSystems.Windows, ControlVendors.Amd), [Capability], GpuAttachment: ControlGpuAttachments.Integrated);

    [Fact]
    public async Task ProbeAndRestoreKeepEachTargetsFirstValueAcrossWriterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-igpu-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new Helper(root, 8))
            {
                var range = Assert.IsType<ControlNumberRange>(first.Writer.Probe(Target("gpu:A"), Capability).Range);
                Assert.Null(range.DefaultValue);
                Assert.Equal(ControlApplyStatuses.Applied,
                    (await first.Writer.WriteAsync(Target("gpu:A"), Capability, new(Capability.Id, Number: -20), default)).Status);
                Assert.Equal(8, first.Writer.Probe(Target("gpu:A"), Capability).Range!.DefaultValue);
            }
            await using (var restarted = new Helper(root, -20))
            {
                var original = Capability with { Range = restarted.Writer.Probe(Target("gpu:A"), Capability).Range };
                Assert.Equal(8, original.Range!.DefaultValue);
                Assert.True(await ((IControlWriter)restarted.Writer).RestoreAsync(Target("gpu:A"), original, new HashSet<string>(), default));
                Assert.Equal(8, (await restarted.Writer.ReadAsync(Target("gpu:A"), original, default))!.Number);
            }
            await using (var changed = new Helper(root, -4))
            {
                var own = Capability with { Range = changed.Writer.Probe(Target("gpu:B"), Capability).Range };
                Assert.Null(own.Range!.DefaultValue);
                await changed.Writer.WriteAsync(Target("gpu:B"), own, new(Capability.Id, Number: -20), default);
                Assert.True(await ((IControlWriter)changed.Writer).RestoreAsync(Target("gpu:B"), own, new HashSet<string>(), default));
                Assert.Equal(-4, (await changed.Writer.ReadAsync(Target("gpu:B"), own, default))!.Number);
            }
        }
        finally { DeleteOwnedFiles(root); }
    }

    [Fact]
    public async Task UnattributedLegacyScalarDisablesAdjustmentWithoutOverwritingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-igpu-baseline-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "UserData", "Control", "amd-igpu-curve-optimizer-baseline.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "8");
            await using var helper = new Helper(root, -20);
            var availability = helper.Writer.Probe(Target("gpu:B"), Capability);
            Assert.False(availability.CanWrite);
            Assert.True(availability.CanRead);
            Assert.Null(availability.Range?.DefaultValue);
            Assert.Equal("8", File.ReadAllText(path));
            Assert.Equal(-20, (await helper.Writer.ReadAsync(Target("gpu:B"), Capability, default))!.Number);
        }
        finally { DeleteOwnedFiles(root); }
    }

    private sealed class Helper : IAsyncDisposable
    {
        private readonly HardwareBridgeClient bridge;
        private Process? observer;
        internal IntegratedGpuControlWriter Writer { get; }
        internal Helper(string root, int initial)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", "hardware-bridge")).FullName;
            File.WriteAllText(Path.Combine(directory, "ResourceManager.HardwareBridge.exe"), "factory placeholder, never executed");
            var script = $$"""
                $ErrorActionPreference='Stop'
                $counts={{initial}}
                $iterations=0
                while ($null -ne ($line=[Console]::In.ReadLine()) -and $iterations -lt 100) {
                    $iterations++
                    $request=$line | ConvertFrom-Json
                    if ($request.op -eq 'set-igpu-curve-optimizer') { $counts=$request.value }
                    [Console]::Out.WriteLine((@{id=$request.id;ok=$true;available=$true;integratedGpuCurveOptimizer=$true;accepted=$true;integratedGpuCurveOptimizerCounts=$counts} | ConvertTo-Json -Compress))
                    [Console]::Out.Flush()
                }
                """;
            Process? Start(ProcessStartInfo ignored)
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                    info.ArgumentList.Add(argument);
                var process = Process.Start(info)!;
                observer = Process.GetProcessById(process.Id);
                _ = observer.SafeHandle;
                return process;
            }
            var environment = new EnvironmentInfo(root);
            bridge = new HardwareBridgeClient(environment, Start);
            Writer = new IntegratedGpuControlWriter(bridge, new Consent(), environment);
        }
        public async ValueTask DisposeAsync()
        {
            bridge.Dispose();
            if (observer is null) return;
            try
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, observer.ExitCode);
            }
            finally { observer.Dispose(); }
        }
    }
    private static void DeleteOwnedFiles(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in new[] { "Dependencies/hardware-bridge/ResourceManager.HardwareBridge.exe",
                     "UserData/Control/amd-igpu-curve-optimizer-baseline.txt", "UserData/Control/amd-igpu-curve-optimizer-baseline.txt.tmp" })
            if (File.Exists(Path.Combine(root, file))) File.Delete(Path.Combine(root, file));
        foreach (var directory in new[] { "Dependencies/hardware-bridge", "Dependencies", "UserData/Control", "UserData" })
            if (Directory.Exists(Path.Combine(root, directory))) Directory.Delete(Path.Combine(root, directory));
        Directory.Delete(root);
    }
    private sealed class Consent : IControlOverclockConsent
    {
        public Task<bool> IsAcceptedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SetAcceptedAsync(bool accepted, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class EnvironmentInfo(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OwnedAmdIgpuTest";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

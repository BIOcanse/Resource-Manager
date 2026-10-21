using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanControlRestoreProtocolTests
{
    [Theory]
    [InlineData("null", false, false)]
    [InlineData("40", false, false)]
    [InlineData("99.9", false, false)]
    [InlineData("100", false, true)]
    [InlineData("100", true, false)]
    [InlineData("0", true, true, false)]
    public async Task MaximumCoolingReceiptRequiresTheActualFullDuty(string duty, bool automatic,
        bool expected, bool wantsMaximum = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-fan-maximum-" + Guid.NewGuid().ToString("N"));
        var helperDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", "fan-control-core")).FullName;
        File.WriteAllText(Path.Combine(helperDirectory, "FanControlCore.exe"), "factory placeholder");
        var script = $$"""
            while ($null -ne ($line = [Console]::In.ReadLine())) {
                $request=$line | ConvertFrom-Json
                if ($request.op -eq 'describe') {
                    $response=@{id=$request.id; ok=$true; available=$true; backend='fixture';
                        fans=@(@{index=0; name='Fixture'; writable=$true; supportsDuty=$true; role='cpu'})}
                    [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 5 -Compress))
                } else {
                    $response=@{id=$request.id; ok=$true; fans=@(@{index=0;
                        automaticControl=${{automatic.ToString().ToLowerInvariant()}}; dutyPercent={{(duty == "null" ? "$null" : duty)}}})}
                    [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 5 -Compress))
                }
                [Console]::Out.Flush()
            }
            """;
        Process? observer = null;
        Process? Start(ProcessStartInfo productionStart)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var process = Process.Start(info)!;
            observer = Process.GetProcessById(process.Id);
            _ = observer.SafeHandle;
            return process;
        }
        using var client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")), Start);
        try
        {
            var target = new ControlObject("fan:oem0", ControlObjectKinds.Fan, "Fixture fan",
                new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);
            var capability = new ControlCapability("fan.lock-maximum", "Maximum", ControlValueKinds.Toggle, true);
            var outcome = await new FanControlWriter(client).WriteAsync(target, capability,
                new("fan.lock-maximum", Toggle: wantsMaximum), default);
            Assert.Equal(expected ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed, outcome.Status);
            if (!expected && !automatic)
                Assert.Equal("写下去了，但回读的值没有变。", outcome.Message);
        }
        finally
        {
            client.Dispose();
            if (observer is not null)
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observer.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("fan.curve", 1, true, false)]
    [InlineData("fan.curve", 0, false, false)]
    [InlineData("fan.curve", -1, false, false)]
    [InlineData("fan.curve", 1, true, true)]
    [InlineData("fan.duty", -1, true, false)]
    [InlineData("fan.lock-maximum", -1, true, false)]
    [InlineData("fan.curve", 1, false, false, 0)]
    [InlineData("fan.duty", -1, false, false, 0)]
    [InlineData("fan.lock-maximum", -1, false, false, -1)]
    [InlineData("fan.curve", -1, true, false, 1, "software")]
    public async Task CurveReleaseRequiresExplicitRestoreReceiptFromHelper(string capability, int acknowledgement, bool expected, bool retainOther,
        int restoreSupport = 1, string? execution = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-fan-restore-" + Guid.NewGuid().ToString("N"));
        var helperDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", "fan-control-core")).FullName;
        File.WriteAllText(Path.Combine(helperDirectory, "FanControlCore.exe"), "factory placeholder");
        var operationsPath = Path.Combine(root, "operations.jsonl");
        var operationsEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(operationsPath));
        var script = $$"""
            $operations=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{operationsEncoded}}'))
            while ($null -ne ($line = [Console]::In.ReadLine())) {
                [IO.File]::AppendAllText($operations,$line+[Environment]::NewLine)
                $request = $line | ConvertFrom-Json
                $response = @{id=$request.id; ok=$true; fans=@(@{index=0; automaticControl=$true})}
                if ($request.op -eq 'describe') {
                    $fan=@{index=0; name='Fixture'; writable=$true; supportsDuty=$true; role='cpu'; firmwareCurve='xy'; supportsSoftwareCurve=$true}
                    if ({{restoreSupport}} -ge 0) { $fan.supportsFirmwareCurveReset = ({{restoreSupport}} -eq 1) }
                    $response.available=$true; $response.backend='fixture'; $response.fans=@($fan)
                }
                if ({{acknowledgement}} -ge 0) { $response.firmwareCurveReset = ({{acknowledgement}} -eq 1 -and $request.resetFirmwareCurve -eq $true) }
                [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 5 -Compress))
                [Console]::Out.Flush()
            }
            """;
        Process? observer = null;
        string? stateDirectory = null;
        Process? Start(ProcessStartInfo productionStart)
        {
            stateDirectory = productionStart.Environment["FANCONTROLCORE_STATE_DIRECTORY"];
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var process = Process.Start(info)!;
            observer = Process.GetProcessById(process.Id);
            _ = observer.SafeHandle;
            return process;
        }
        using var client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")), Start);
        try
        {
            var target = new ControlObject("fan:oem0", ControlObjectKinds.Fan, "Fixture fan",
                new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);
            var writer = new FanControlWriter(client);
            var curve = new ControlCurvePoint[] { new(60, 35), new(95, 100) };
            using var firmwareReceipt = System.Text.Json.JsonDocument.Parse("{\"execution\":\"firmware\"}");
            client.StoreCurveReceipt(0, firmwareReceipt.RootElement, curve);
            Assert.Same(curve, await client.ReadCurveAsync(0, default));
            var result = await writer.RestoreAsync(target, new(capability, capability, ControlValueKinds.Curve, true),
                retainOther ? new HashSet<string> { "fan.lock-maximum" } : new HashSet<string>(), default,
                execution is null ? null : new ControlSetting(capability, CurveExecution: execution));
            Assert.Equal(expected, result);
            var operations = File.ReadAllLines(operationsPath).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
            var sentAutomatic = false;
            try
            {
                var auto = operations.Where(op => op.RootElement.GetProperty("op").GetString() == "auto").ToArray();
                sentAutomatic = auto.Length > 0;
                if (restoreSupport <= 0)
                {
                    Assert.Empty(auto);
                    var outcome = await writer.WriteAsync(target, new(capability, capability, ControlValueKinds.Toggle, true),
                        new(capability, Number: 40, Toggle: true, Curve: curve), default);
                    Assert.Equal(ControlApplyStatuses.Failed, outcome.Status);
                    Assert.Equal(FanControlWriter.RestoreProtocolRequired, outcome.Message);
                    Assert.Equal(operations.Length, File.ReadAllLines(operationsPath).Length);
                    var probe = writer.Probe(target, new(capability, capability, ControlValueKinds.Toggle, true));
                    Assert.False(probe.CanWrite); Assert.True(probe.CanRead);
                    Assert.Equal(ControlUnavailableKinds.Prerequisite, probe.UnavailableKind);
                }
                else
                {
                    var sent = Assert.Single(auto).RootElement.GetProperty("resetFirmwareCurve").GetBoolean();
                    Assert.Equal(capability == "fan.curve" && execution != "software", sent);
                }
            }
            finally { foreach (var operation in operations) operation.Dispose(); }
            if (sentAutomatic) Assert.Null(await client.ReadCurveAsync(0, default));
            else Assert.Same(curve, await client.ReadCurveAsync(0, default));
            Assert.Equal(Path.Combine(root, "UserData", "Control", "FanControlCore"), stateDirectory);
            if (execution == "software")
            {
                client.ResetDetection();
                Assert.True(Assert.Single(client.Describe()).SupportsFirmwareCurveReset);
                using var refreshed = System.Text.Json.JsonDocument.Parse(File.ReadAllLines(operationsPath).Last());
                Assert.Equal("describe", refreshed.RootElement.GetProperty("op").GetString());
                Assert.True(refreshed.RootElement.GetProperty("refresh").GetBoolean());
                var requestCount = File.ReadAllLines(operationsPath).Length;
                client.Describe();
                Assert.Equal(requestCount, File.ReadAllLines(operationsPath).Length);
            }
        }
        finally
        {
            client.Dispose();
            if (observer is not null)
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observer.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class EnvironmentInfo(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "OwnedFanRestoreTest";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

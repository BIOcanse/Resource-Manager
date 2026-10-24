using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class FanCurveReceiptTests
{
    private const string CompleteCurve = "[{\"temperatureCelsius\":60,\"percent\":35},{\"temperatureCelsius\":120,\"percent\":100}]";

    public static IEnumerable<object[]> InvalidCurves()
    {
        foreach (var curve in new[]
        {
            "[]", "[{}]", "[null]",
            "[{\"temperatureCelsius\":60,\"percent\":35},{}]",
            "[{\"temperatureCelsius\":1e400,\"percent\":35}]",
            "[{\"temperatureCelsius\":60,\"percent\":1e400}]",
            "[{\"temperatureCelsius\":60,\"percent\":-1}]",
            "[{\"temperatureCelsius\":60,\"percent\":101}]"
        }) yield return [curve];
    }

    [Theory]
    [MemberData(nameof(InvalidCurves))]
    public async Task IncompleteWriteReceiptDoesNotReportAppliedAndRequiresAFreshCompleteRead(string curve)
    {
        await using var helper = new CurveHelper($"\"ok\":true,\"curve\":{curve}");
        var result = await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35), new(120, 100)], CurveExecution: ControlFanCurveExecutions.Software), default);

        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Equal("写下去了，但回读的值没有变。", result.Message);
        var actual = await helper.Client.ReadCurveAsync(0, default);
        Assert.Equal(new ControlCurvePoint[] { new(60, 35), new(120, 100) }, actual);
        Assert.Equal(1, File.ReadAllLines(helper.Operations).Count(operation => operation == "curve"));
    }

    [Theory]
    [MemberData(nameof(InvalidCurves))]
    public Task IncompleteReadReceiptIsUnavailableAndTheNextCompleteReadRecovers(string curve)
        => RejectReadAndRecoverAsync($"\"ok\":true,\"curve\":{curve}");

    [Fact]
    public Task FailedReadReceiptCannotSupplyACurve()
        => RejectReadAndRecoverAsync($"\"ok\":false,\"curve\":{CompleteCurve}");

    [Fact]
    public async Task ExplicitReadRevalidatesFirmwareAfterASuccessfulWrite()
    {
        await using var helper = new CurveHelper($"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}");
        var result = await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 30), new(95, 90)], CurveExecution: ControlFanCurveExecutions.Firmware), default);

        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
        var actual = await helper.Client.ReadCurveAsync(0, default);
        Assert.Equal(new ControlCurvePoint[] { new(60, 35), new(120, 100) }, actual);
        Assert.Equal(actual, await helper.Client.ReadCurveAsync(0, default));
        Assert.Equal(2, File.ReadAllLines(helper.Operations).Count(operation => operation == "curve"));
        Assert.Contains("setcurve", File.ReadAllLines(helper.Operations));
    }

    [Fact]
    public async Task SoftwareWriteReceiptDoesNotOutliveTheCoresActivePlan()
    {
        await using var helper = new CurveHelper($"\"ok\":true,\"execution\":\"software\",\"curve\":{CompleteCurve}",
            "\"ok\":true,\"execution\":null,\"curve\":null");
        var result = await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35), new(120, 100)], CurveExecution: ControlFanCurveExecutions.Software), default);
        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
        Assert.Null(await helper.Client.ReadCurveAsync(0, default));
        Assert.Contains("curve", File.ReadAllLines(helper.Operations));
    }

    [Fact]
    public async Task SoftwareReadReceiptIsReadAgainAfterTheCoreStopsItsPlan()
    {
        await using var helper = new CurveHelper($"\"ok\":true,\"execution\":\"software\",\"curve\":{CompleteCurve}",
            "\"ok\":true,\"execution\":null,\"curve\":null");
        Assert.NotNull(await helper.Client.ReadCurveAsync(0, default));
        Assert.Null(await helper.Client.ReadCurveAsync(0, default));
        Assert.Equal(2, File.ReadAllLines(helper.Operations).Count(operation => operation == "curve"));
    }

    private static async Task RejectReadAndRecoverAsync(string firstReceipt)
    {
        await using var helper = new CurveHelper(firstReceipt);
        Assert.Null(await helper.Client.ReadCurveAsync(0, default));
        var actual = await helper.Client.ReadCurveAsync(0, default);
        Assert.Equal(new ControlCurvePoint[] { new(60, 35), new(120, 100) }, actual);
        Assert.Equal(actual, await helper.Client.ReadCurveAsync(0, default));
        Assert.Equal(3, File.ReadAllLines(helper.Operations).Count(operation => operation == "curve"));
    }

    [Theory]
    [InlineData("duty", true)]
    [InlineData("duty", false)]
    [InlineData("boost", true)]
    [InlineData("boost", false)]
    [InlineData("auto", true)]
    [InlineData("auto", false)]
    [InlineData("release-duty", true)]
    [InlineData("release-duty", false)]
    [InlineData("release-curve", true)]
    [InlineData("release-curve", false)]
    public async Task ManualWritesAndRestorationNeverReplaceAnExplicitFreshRead(string action, bool confirmed)
    {
        var automatic = action is "auto" or "release-duty" or "release-curve";
        var receipt = $"\"ok\":{confirmed.ToString().ToLowerInvariant()},\"firmwareCurveReset\":true,\"fans\":[{{\"index\":0,\"automaticControl\":{automatic.ToString().ToLowerInvariant()},\"dutyPercent\":100}}]";
        var changed = new ControlCurvePoint[] { new(60, 55), new(120, 100) };
        await using var helper = new CurveHelper($"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}",
            "\"ok\":true,\"execution\":\"firmware\",\"curve\":[{\"temperatureCelsius\":60,\"percent\":55},{\"temperatureCelsius\":120,\"percent\":100}]", receipt);
        var previous = await helper.Client.ReadCurveAsync(0, default);
        Assert.NotNull(previous);
        var id = action is "duty" or "release-duty" ? "fan.duty"
            : action == "release-curve" ? "fan.curve" : "fan.lock-maximum";
        var capability = new ControlCapability(id, id, id == "fan.curve" ? ControlValueKinds.Curve
            : id == "fan.duty" ? ControlValueKinds.Number : ControlValueKinds.Toggle, true);
        var writer = new FanControlWriter(helper.Client);
        if (action.StartsWith("release-", StringComparison.Ordinal))
            Assert.Equal(confirmed, await writer.RestoreAsync(Target, capability, new HashSet<string>(), default,
                new(id, CurveExecution: ControlFanCurveExecutions.Firmware)));
        else
        {
            var outcome = await writer.WriteAsync(Target, capability, new(id, Number: 55, Toggle: action == "boost"), default);
            Assert.Equal(confirmed ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed, outcome.Status);
        }
        // A failed command can still have modified part of the table. Every edit reads again.
        var actual = await helper.Client.ReadCurveAsync(0, default);
        Assert.Equal(changed, actual);
        Assert.NotSame(previous, actual);
        Assert.Equal(actual, await helper.Client.ReadCurveAsync(0, default));
        Assert.Equal(changed, await helper.Client.ReadCurveAsync(1, default));
        Assert.Equal(4, File.ReadAllLines(helper.Operations).Count(operation => operation == "curve"));
    }

    private static ControlObject Target => new("fan:oem0", ControlObjectKinds.Fan, "Fixture",
        new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);

    // Only a finite owned JSON process. No real fan core, sensors or hardware.
    private sealed class CurveHelper : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-curve-receipt-" + Guid.NewGuid().ToString("N"));
        private readonly string helperDirectory;
        private Process? observer;
        internal string Operations { get; }
        internal FanControlCoreClient Client { get; }

        internal CurveHelper(string firstReceipt, string? laterReceipt = null, string? manualReceipt = null, bool requireSeed = false)
        {
            laterReceipt ??= $"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}";
            helperDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", "fan-control-core")).FullName;
            File.WriteAllText(Path.Combine(helperDirectory, "FanControlCore.exe"), "factory placeholder");
            Operations = Path.Combine(root, "operations.txt");
            var script = $$"""
                $count = 0
                $seeded = $false
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    $request = $line | ConvertFrom-Json
                    [IO.File]::AppendAllText('{{Operations.Replace("'", "''")}}', $request.op + [Environment]::NewLine)
                    if ($request.op -eq 'exit-fixture') {
                        [Console]::Out.WriteLine('{"id":' + $request.id + ',"ok":true}')
                        [Console]::Out.Flush()
                        break
                    } elseif ($request.op -eq 'temp') {
                        $seeded = $null -ne $request.samples.cpu -and $request.observedAtUtcTicks.cpu -gt 0
                        $body = '"ok":true'
                    } elseif ($request.op -eq 'setcurve' -and {{(requireSeed ? "$true" : "$false")}} -and -not $seeded) {
                        $body = '"ok":false,"error":"temperature required"'
                    } elseif ($request.op -eq 'describe') {
                        $body = '"ok":true,"available":true,"backend":"fixture","needsTemperatures":true,"fans":[{"index":0,"name":"Fixture","role":"cpu","writable":true,"supportsSoftwareCurve":true,"firmwareCurve":"xy","supportsFirmwareCurveReset":true},{"index":1,"name":"Other","role":"gpu","writable":true,"supportsSoftwareCurve":true,"firmwareCurve":"xy","supportsFirmwareCurveReset":true}]'
                    } elseif ($request.op -in @('set','boost','auto') -and '{{manualReceipt}}' -ne '') {
                        $body = '{{manualReceipt}}'
                    } else {
                        $count++
                        $body = if ($count -eq 1) { '{{firstReceipt}}' } else { '{{laterReceipt}}' }
                    }
                    [Console]::Out.WriteLine('{"id":' + $request.id + ',' + $body + '}')
                    [Console]::Out.Flush()
                }
                """;
            Process? Start(ProcessStartInfo productionStart)
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                var child = Process.Start(start)!;
                observer = Process.GetProcessById(child.Id);
                _ = observer.SafeHandle;
                return child;
            }
            Client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")), Start);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (observer is not null)
            {
                try { await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { observer.Dispose(); }
            }
            File.Delete(Operations);
            File.Delete(Path.Combine(helperDirectory, "FanControlCore.exe"));
            Directory.Delete(helperDirectory);
            Directory.Delete(Path.Combine(root, "Dependencies"));
            Directory.Delete(root);
        }
    }

    private sealed class EnvironmentInfo(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}



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
        Directory.CreateDirectory(root);
        using var client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")),
            (_, _) => new DelegateFanSession(request =>
            {
                var response = request.GetProperty("op").GetString() == "describe"
                    ? """{"ok":true,"available":true,"backend":"fixture","fans":[{"index":0,"name":"Fixture","writable":true,"supportsDuty":true,"role":"cpu"}]}"""
                    : "{\"ok\":true,\"fans\":[{\"index\":0,\"automaticControl\":" + automatic.ToString().ToLowerInvariant()
                        + ",\"dutyPercent\":" + duty + "}]}";
                return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response);
            }));
        try
        {
            var target = new ControlObject("fan:oem0", ControlObjectKinds.Fan, "Fixture fan",
                new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);
            var capability = new ControlCapability("fan.lock-maximum", ControlValueKinds.Toggle, true);
            var outcome = await new FanControlWriter(client).WriteAsync(target, capability,
                new("fan.lock-maximum", Toggle: wantsMaximum), default);
            Assert.Equal(expected ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed, outcome.Status);
            if (!expected && !automatic)
                Assert.Equal("control-readback-mismatch", outcome.Reason?.Code);
        }
        finally
        {
            client.Dispose();
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
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
        Directory.CreateDirectory(root);
        var operationsPath = Path.Combine(root, "operations.jsonl");
        string? stateDirectory = null;
        using var client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")),
            (state, _) =>
            {
                stateDirectory = state;
                return new DelegateFanSession(request =>
                {
                    File.AppendAllText(operationsPath, request.GetRawText() + Environment.NewLine);
                    var response = new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["fans"] = new[] { new Dictionary<string, object?> { ["index"] = 0, ["automaticControl"] = true } }
                    };
                    if (request.GetProperty("op").GetString() == "describe")
                    {
                        var fan = new Dictionary<string, object?>
                        {
                            ["index"] = 0, ["name"] = "Fixture", ["writable"] = true, ["supportsDuty"] = true,
                            ["role"] = "cpu", ["firmwareCurve"] = "xy", ["supportsSoftwareCurve"] = true
                        };
                        if (restoreSupport >= 0) fan["supportsFirmwareCurveReset"] = restoreSupport == 1;
                        response["available"] = true; response["backend"] = "fixture"; response["fans"] = new[] { fan };
                    }
                    if (acknowledgement >= 0)
                        response["firmwareCurveReset"] = acknowledgement == 1
                            && request.TryGetProperty("resetFirmwareCurve", out var reset) && reset.GetBoolean();
                    return System.Text.Json.JsonSerializer.SerializeToElement(response);
                });
            });
        try
        {
            var target = new ControlObject("fan:oem0", ControlObjectKinds.Fan, "Fixture fan",
                new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);
            var writer = new FanControlWriter(client);
            var curve = new ControlCurvePoint[] { new(60, 35), new(95, 100) };
            var result = await writer.RestoreAsync(target, new(capability, ControlValueKinds.Curve, true),
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
                    var outcome = await writer.WriteAsync(target, new(capability, ControlValueKinds.Toggle, true),
                        new(capability, Number: 40, Toggle: true, Curve: curve), default);
                    Assert.Equal(ControlApplyStatuses.Failed, outcome.Status);
                    Assert.Equal(FanControlWriter.RestoreProtocolRequired, outcome.Reason?.Code);
                    Assert.Equal(operations.Length, File.ReadAllLines(operationsPath).Length);
                    var probe = writer.Probe(target, new(capability, ControlValueKinds.Toggle, true));
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
            Assert.Null(await client.ReadCurveAsync(0, default));
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
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
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

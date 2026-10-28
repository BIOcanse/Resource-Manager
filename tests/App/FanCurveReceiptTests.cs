

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
            new("fan.curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35), new(120, 100)], CurveExecution: ControlFanCurveExecutions.Software), default);

        Assert.Equal(ControlApplyStatuses.Failed, result.Status);
        Assert.Equal("control-readback-mismatch", result.Reason?.Code);
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
            new("fan.curve", ControlValueKinds.Curve, true),
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
        await using var helper = new CurveHelper($"\"ok\":true,\"softwarePlanRecorded\":true,\"execution\":null,\"curve\":{CompleteCurve}",
            "\"ok\":true,\"execution\":null,\"curve\":null");
        var result = await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35), new(120, 100)], CurveExecution: ControlFanCurveExecutions.Software), default);
        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
        Assert.Null(await helper.Client.ReadCurveAsync(0, default));
        Assert.Contains("curve", File.ReadAllLines(helper.Operations));
    }

    [Fact]
    public async Task RecordedSoftwarePlanIsAppliedWithoutAnExecutionOrCurveReadback()
    {
        await using var helper = new CurveHelper("\"ok\":true,\"softwarePlanRecorded\":true,\"execution\":null,\"curve\":null");
        var result = await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35)], CurveExecution: ControlFanCurveExecutions.Software), default);
        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
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
        var capability = new ControlCapability(id, id == "fan.curve" ? ControlValueKinds.Curve
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

    // In-memory protocol fixture; no child processes, sensors or hardware.
    private sealed class CurveHelper : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-curve-receipt-" + Guid.NewGuid().ToString("N"));
        internal string Operations { get; }
        internal FanControlCoreClient Client { get; }

        internal CurveHelper(string firstReceipt, string? laterReceipt = null, string? manualReceipt = null, bool requireSeed = false)
        {
            Directory.CreateDirectory(root);
            Operations = Path.Combine(root, "operations.txt");
            laterReceipt ??= $"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}";
            var count = 0;
            var seeded = false;
            var sync = new object();
            Client = new FanControlCoreClient(new EnvironmentInfo(Path.Combine(root, "ResourceManager-APP")),
                (_, _) => new DelegateFanSession(request =>
                {
                    lock (sync)
                    {
                        var operation = request.GetProperty("op").GetString()!;
                        File.AppendAllLines(Operations, [operation]);
                        string body;
                        if (operation == "exit-fixture") throw new IOException("Injected library failure");
                        if (operation == "temp")
                        {
                            seeded = request.GetProperty("samples").TryGetProperty("cpu", out _)
                                && request.GetProperty("observedAtUtcTicks").GetProperty("cpu").GetInt64() > 0;
                            body = "\"ok\":true";
                        }
                        else if (operation == "setcurve" && requireSeed && !seeded)
                            body = "\"ok\":false,\"error\":\"temperature required\"";
                        else if (operation == "describe")
                            body = """
                                "ok":true,"available":true,"backend":"fixture","needsTemperatures":true,"fans":[{"index":0,"name":"Fixture","role":"cpu","writable":true,"supportsSoftwareCurve":true,"firmwareCurve":"xy","supportsFirmwareCurveReset":true},{"index":1,"name":"Other","role":"gpu","writable":true,"supportsSoftwareCurve":true,"firmwareCurve":"xy","supportsFirmwareCurveReset":true}]
                                """;
                        else if (operation is "set" or "boost" or "auto" && manualReceipt is not null) body = manualReceipt;
                        else body = ++count == 1 ? firstReceipt : laterReceipt;
                        var id = request.TryGetProperty("id", out var value) ? value.GetInt64() : 0;
                        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"id\":" + id + "," + body + "}");
                    }
                }));
            Client.StartSession().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            File.Delete(Operations);
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

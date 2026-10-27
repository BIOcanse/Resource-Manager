using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanManagementModesTests
{
    private static readonly ControlCapability[] Capabilities = [
        new("fan.rpm", "number", false), new("fan.curve", "curve", true),
        new("fan.duty", "number", true, Range: new(0,100,1,"%")), new("fan.lock-maximum", "toggle", true)];
    private static FanCoreFan Fan(string? firmware = "table", bool software = true, bool reset = true,
        string? prerequisite = null) => new(0, null, "uniwill-wmi-ec", true, true, "cpu", firmware,
            software, true, 10, 0, false, prerequisite, reset);

    [Theory]
    [InlineData("table", true, true, null, true, true)]
    [InlineData(null, true, true, null, false, true)]
    [InlineData("xy", false, true, null, true, true)]
    [InlineData("table", true, false, null, false, true)]
    [InlineData("table", true, true, "custom-mode", false, true)]
    public void ModesPublishMembershipAndExecution(string? firmware, bool software, bool reset,
        string? prerequisite, bool canFirmware, bool canSoftware)
    {
        var modes = FanControlWriter.ManagementModes(Capabilities, Fan(firmware, software, reset, prerequisite));
        Assert.Equal(new[] { "firmware-management", "firmware-write", "software-management" }, modes.Select(mode => mode.Id));
        Assert.True(modes[0].Available); Assert.Empty(modes[0].Items);
        Assert.Equal(canFirmware, modes[1].Available); Assert.Equal(canSoftware, modes[2].Available);
        Assert.Equal(new ControlFanManagementItem("fan.curve", "firmware"), Assert.Single(modes[1].Items));
        Assert.Equal(software, modes[2].Items.Any(item => item.CapabilityId == "fan.curve" && item.CurveExecution == "software"));
        Assert.DoesNotContain(modes.SelectMany(mode => mode.Items), item => item.CapabilityId == "fan.rpm");
        foreach (var mode in modes.Where(mode => !mode.Available)) { Assert.NotNull(mode.UnavailableReason); Assert.NotNull(mode.UnavailableKind); }
    }

    [Fact]
    public void CatalogPublishesModesFromFakeCoreWithoutWriting()
    {
        using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(request => {
            Assert.Equal("describe", request.GetProperty("op").GetString());
            return JsonSerializer.SerializeToElement(new { ok = true, available = true, backend = "uniwill-wmi-ec",
                fans = new[] { new { index = 0, writable = true, supportsDuty = true, role = "cpu", firmwareCurve = "table",
                    supportsSoftwareCurve = true, softwareTakeoverCoversAllFans = true, curvePoints = 10, supportsFirmwareCurveReset = true } } });
        }));
        var writer = new FanControlWriter(client);
        var catalog = new WindowsControlObjectCatalog(null!, [writer], new Access(), client, null!, null!, null!);
        var target = new ControlObject("fan:oem0", "fan", null, new("windows", "oem"), Capabilities);
        var result = catalog.ResolveAvailability(target);
        Assert.Equal(3, result.FanManagementModes!.Count);
        Assert.All(result.FanManagementModes, mode => Assert.True(mode.Available));
        Assert.Equal("table", result.FirmwareCurveKind);
        Assert.True(result.SoftwareTakeoverCoversAllFans);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(3, json.GetProperty("fanManagementModes").GetArrayLength());
    }

    [Fact]
    public void ReadOnlyFanKeepsFirmwareManagementAvailable()
    {
        var modes = FanControlWriter.ManagementModes(Capabilities.Select(c => c with {
            Supported = false, UnavailableKind = "platform", UnavailableReason = "fan-firmware-read-only" }).ToArray(), Fan());
        Assert.True(modes[0].Available); Assert.All(modes.Skip(1), mode => Assert.False(mode.Available));
    }

    [Fact]
    public void FirmwareOnlyCurveCannotMakeSoftwareManagementAvailable()
    {
        var capabilities = Capabilities.Select(capability => capability.Id == "fan.curve" ? capability
            : capability with { Supported = false, UnavailableKind = "platform", UnavailableReason = "fan-duty-unsupported" }).ToArray();
        var modes = FanControlWriter.ManagementModes(capabilities, Fan(software: false));
        Assert.True(modes[1].Available);
        Assert.False(modes[2].Available);
        Assert.DoesNotContain(modes[2].Items, item => item.CapabilityId == "fan.curve");
    }

    [Theory]
    [InlineData("fan.duty")]
    [InlineData("fan.lock-maximum")]
    [InlineData("fan.target-rpm")]
    public async Task ApplyRejectsMixedFanBeforeAnyWriterCall(string softwareId)
    {
        var writer = new Writer(); var layer = new ControlWriteLayer(new Catalog(), [writer], new Access());
        var report = await layer.WriteAsync(new([new("fan:test", [new("fan.curve", CurveExecution: "firmware"), new(softwareId, Number: 42)])]), default);
        Assert.Equal(2, report.Outcomes.Count);
        Assert.All(report.Outcomes, result => Assert.Equal("fan-management-modes-mixed", result.Reason?.Code));
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task StoredMixedStateIsReportedUnchangedAndNeverAppliedOnStartup()
    {
        var original = new ControlDesiredState([new("fan:test", [new("fan.curve", CurveExecution: "firmware"), new("fan.duty", Number: 42)])]);
        var store = new Store(JsonSerializer.Deserialize<ControlDesiredState>(JsonSerializer.Serialize(original))!);
        var writer = new Writer(); var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(), [writer], new Access()));
        await plane.InitializeAsync(default);
        var view = await plane.ReadStateAsync(default);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(view.Desired));
        Assert.Equal(2, view.LastApply.Outcomes.Count); Assert.Empty(writer.Calls); Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("firmware", "software", false)]
    [InlineData("software", "firmware", false)]
    [InlineData("firmware", null, false)]
    [InlineData("software", null, false)]
    [InlineData("firmware", "software", true)]
    public async Task SwitchingCurveExecutionReleasesPreviousBeforeWriting(string before, string? after, bool failRelease)
    {
        var store = new Store(new([new("fan:test", [new("fan.curve", CurveExecution: before)])]));
        var writer = new Writer { FailRelease = failRelease };
        var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(), [writer], new Access()));
        var view = await plane.SetObjectSettingsAsync("fan:test", after is null ? [] : [new("fan.curve", CurveExecution: after)], default);
        Assert.Equal("release:" + before, writer.Calls[0]);
        if (failRelease)
        {
            Assert.Single(writer.Calls); Assert.Single(view.Desired.PendingReleases);
            writer.FailRelease = false;
            var retried = await plane.RetryObjectAsync("fan:test", default);
            Assert.Empty(retried.Desired.PendingReleases);
            Assert.Equal(new[] { "release:firmware", "release:firmware", "write:software" }, writer.Calls);
        }
        else { Assert.Empty(view.Desired.PendingReleases); Assert.Equal(after is null ? 1 : 2, writer.Calls.Count); }
    }

    [Theory]
    [InlineData("fan.duty")]
    [InlineData("fan.lock-maximum")]
    public async Task LeavingSoftwareItemsForFirmwareCurveEndsTheTemporaryTakeover(string previousId)
    {
        var store = new Store(new([new("fan:test", [new(previousId, Number: 42, Unit: "%", Toggle: true)])]));
        var writer = new Writer(); var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(), [writer], new Access()));
        await plane.SetObjectSettingsAsync("fan:test", [new("fan.curve", CurveExecution: "firmware")], default);
        Assert.Equal(new[] { "release:", "write:firmware" }, writer.Calls);
        Assert.Empty(Assert.Single(writer.Retained));
    }

    [Fact]
    public async Task AnotherFanCanBeEditedWithoutRewritingOrApplyingTheStoredMixedFan()
    {
        var mixed = new ControlObjectDesiredState("fan:test", [new("fan.curve", CurveExecution: "firmware"), new("fan.duty", Number: 42)]);
        var store = new Store(new([mixed])); var writer = new Writer();
        var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(), [writer], new Access()));
        var view = await plane.SetObjectSettingsAsync("fan:other", [new("fan.duty", Number: 50, Unit: "%")], default);
        Assert.Single(writer.Calls);
        Assert.Same(mixed, view.Desired.ForObject("fan:test"));
        Assert.All(view.LastApply.Outcomes, outcome => Assert.Equal("fan:other", outcome.ObjectId));
    }

    [Fact]
    public async Task RejectingMixedCardPreservesTheOtherCardsReceipt()
    {
        var store = new Store(ControlDesiredState.Empty); var writer = new Writer();
        var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(), [writer], new Access()));
        await plane.SetObjectSettingsAsync("fan:other", [new("fan.duty", Number: 50, Unit: "%")], default);
        var view = await plane.SetObjectSettingsAsync("fan:test", [new("fan.curve", CurveExecution: "firmware"), new("fan.lock-maximum", Toggle: true)], default);
        Assert.Single(writer.Calls);
        Assert.Equal("applied", Assert.Single(view.LastApply.Outcomes, outcome => outcome.ObjectId == "fan:other").Status);
        Assert.All(view.LastApply.Outcomes.Where(outcome => outcome.ObjectId == "fan:test"), outcome =>
            Assert.Equal("fan-management-modes-mixed", outcome.Reason?.Code));
    }

    [Fact]
    public async Task MixedReplacementDoesNotReleaseExistingSoftwareState()
    {
        var store = new Store(new([new("fan:test", [new("fan.duty", Number: 42, Unit: "%")]) ]));
        var writer = new Writer(); var plane = new ControlPlane(store, new ControlWriteLayer(new Catalog(), [writer], new Access()));
        var view = await plane.SetObjectSettingsAsync("fan:test", [new("fan.curve", CurveExecution: "firmware"), new("fan.lock-maximum", Toggle: true)], default);
        Assert.Empty(writer.Calls); Assert.Equal(0, store.Saves); Assert.Single(view.Desired.Objects[0].Settings);
        Assert.All(view.LastApply.Outcomes, item => Assert.Equal("fan-management-modes-mixed", item.Reason?.Code));
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => new([new("fan:test", "fan", null, new("windows","oem"), Capabilities),
            new("fan:other", "fan", null, new("windows","oem"), Capabilities)], DateTimeOffset.UtcNow);
    }
    private sealed class Access : IControlAccessLevel
    { public string Current => "normal"; public Task SetAsync(string value, CancellationToken token) => Task.CompletedTask; }
    private sealed class Store(ControlDesiredState value) : IControlDesiredStateStore
    {
        public ControlDesiredState Value = value; public int Saves;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token) { Value = value; Saves++; return Task.CompletedTask; }
    }
    private sealed class Writer : IControlWriter
    {
        public List<string> Calls = []; public bool FailRelease; public List<IReadOnlySet<string>> Retained = [];
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability) => ControlWriteAvailability.Yes(capability.Range);
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
        { Calls.Add("write:" + setting.CurveExecution); return Task.FromResult(new ControlApplyOutcome(target.Id, capability.Id, "applied")); }
        public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> retained, CancellationToken token, ControlSetting? previousSetting = null)
        { Calls.Add("release:" + previousSetting?.CurveExecution); Retained.Add(retained); return Task.FromResult(!FailRelease); }
    }
}

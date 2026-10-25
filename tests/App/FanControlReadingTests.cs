using System.Text.Json;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanControlReadingTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData(",\"supportsFirmwareCurveReset\":false", false)]
    [InlineData(",\"supportsFirmwareCurveReset\":true", true)]
    public void OriginalTableRestoreRequiresExplicitCapability(string field, bool expected)
    {
        using var document = JsonDocument.Parse("[{\"index\":0,\"name\":\"CPU\",\"writable\":true,\"firmwareCurve\":\"table\"" + field + "}]");
        Assert.Equal(expected, Assert.Single(FanControlCoreClient.ParseFans(document.RootElement, "fixture")).SupportsFirmwareCurveReset);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("custom mode required", "fan-firmware-prerequisite")]
    public void DescriptionPreservesFirmwarePrerequisiteAndTakeoverScope(string? prerequisite, string? expectedCode)
    {
        using var description = JsonSerializer.SerializeToDocument(new[] { new {
            index = 0, name = "CPU", writable = true, role = "cpu", firmwareCurve = "table",
            firmwareCurveRequires = prerequisite, softwareTakeoverCoversAllFans = true
        } });
        var fan = Assert.Single(FanControlCoreClient.ParseFans(description.RootElement, "fixture"));
        Assert.Equal(expectedCode, fan.FirmwareCurveRequires);
        Assert.Equal(ControlFanRoles.Cpu, fan.Role);
        Assert.True(fan.SoftwareTakeoverCoversAllFans);
        Assert.False(fan.SupportsSoftwareCurve);
    }

    private static readonly ControlObject Target = new("fan:oem:1", "fan", "Fan", new("windows", "oem"), []);

    [Theory]
    [InlineData("{\"automaticControl\":false,\"dutyPercent\":40}", false)]
    [InlineData("{\"automaticControl\":false,\"dutyPercent\":100}", true)]
    [InlineData("{\"automaticControl\":true,\"dutyPercent\":100}", false)]
    [InlineData("{\"automaticControl\":false}", null)]
    [InlineData("{\"automaticControl\":false,\"dutyPercent\":1e999}", null)]
    [InlineData("{\"dutyPercent\":100}", null)]
    public void ManualDutyDoesNotMeanMaximumCooling(string json, bool? expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = FanControlWriter.ProjectReading(Target, new("fan.lock-maximum", "toggle", true), document.RootElement);
        Assert.Equal(expected, actual.Toggle);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"rpm\":0}", 0d)]
    [InlineData("{\"rpm\":2400}", 2400d)]
    public void RpmPreservesMissingAndZero(string json, double? expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = FanControlWriter.ProjectReading(Target, new("fan.rpm", "number", false), document.RootElement);
        Assert.Equal(expected, actual.Number);
        Assert.Equal("RPM", actual.Unit);
    }
}

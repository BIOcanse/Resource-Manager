using System.Text.Json;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanControlReadingTests
{
    private static readonly ControlObject Target = new("fan:oem:1", "fan", "Fan", new("windows", "oem"), []);

    [Theory]
    [InlineData("{\"automaticControl\":false,\"dutyPercent\":40}", false)]
    [InlineData("{\"automaticControl\":false,\"dutyPercent\":100}", true)]
    [InlineData("{\"automaticControl\":true,\"dutyPercent\":100}", false)]
    [InlineData("{\"automaticControl\":false}", null)]
    [InlineData("{\"dutyPercent\":100}", null)]
    public void ManualDutyDoesNotMeanMaximumCooling(string json, bool? expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = FanControlWriter.ProjectReading(Target, new("fan.lock-maximum", "Boost", "toggle", true), document.RootElement);
        Assert.Equal(expected, actual.Toggle);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"rpm\":0}", 0d)]
    [InlineData("{\"rpm\":2400}", 2400d)]
    public void RpmPreservesMissingAndZero(string json, double? expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = FanControlWriter.ProjectReading(Target, new("fan.rpm", "RPM", "number", false), document.RootElement);
        Assert.Equal(expected, actual.Number);
        Assert.Equal("RPM", actual.Unit);
    }
}

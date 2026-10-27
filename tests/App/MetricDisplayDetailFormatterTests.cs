using System.Text.Json;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed class MetricRawContractTests
{
    [Theory]
    [InlineData("label")]
    [InlineData("group")]
    [InlineData("displayValue")]
    [InlineData("detail")]
    [InlineData("preferredSlot")]
    [InlineData("requiredComponentName")]
    public void BackendMetricContractsContainFactsOnly(string presentationProperty)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var value = JsonDocument.Parse(JsonSerializer.Serialize(new MetricReadingValue("cpu.usage", 25, "%"), options));
        using var definition = JsonDocument.Parse(JsonSerializer.Serialize(new MetricDefinition("cpu.usage", "%", ResourceManager.ReadWrite.ReadVolatility.Sampled), options));
        Assert.False(value.RootElement.TryGetProperty(presentationProperty, out _));
        Assert.False(definition.RootElement.TryGetProperty(presentationProperty, out _));
        Assert.Equal(25, value.RootElement.GetProperty("numericValue").GetInt32());
        Assert.Equal("%", definition.RootElement.GetProperty("unit").GetString());
    }
}

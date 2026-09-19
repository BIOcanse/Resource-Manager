using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class ControlProbeContractTests
{
    [Fact]
    public void RejectedClockWriteCannotAffectSiblingAndReversedBoundsDoNotWrite()
    {
        var writer = new NvidiaGpuControlWriter();
        Assert.Equal(4, writer.WriteClockRange(0, 0, true, 2000, 3000, (_, _) => 4, out _, out _));
        Assert.Equal(0, writer.WriteClockRange(0, 0, false, 1000, 3000, (min, max) =>
        {
            Assert.Equal(0u, min);
            Assert.Equal(1000u, max);
            return 0;
        }, out _, out _));
        Assert.Equal(2, writer.WriteClockRange(0, 0, true, 2000, 3000,
            (_, _) => throw new InvalidOperationException("Invalid pair must not reach hardware"), out _, out _));
        Assert.Equal(0, writer.WriteClockRange(0, 0, true, 500, 3000, (_, _) => 0, out var lower, out var upper));
        Assert.Equal(500u, lower);
        Assert.Equal(1000u, upper);
    }
    [Theory]
    [InlineData("{}", "power", false)]
    [InlineData("{\"ok\":false,\"power\":50}", "power", false)]
    [InlineData("{\"ok\":true}", "power", false)]
    [InlineData("{\"ok\":true,\"power\":\"50\"}", "power", false)]
    [InlineData("{\"ok\":true,\"power\":0}", "power", true)]
    [InlineData("{\"ok\":true,\"power\":50}", "power", true)]
    [InlineData("{\"ok\":true}", null, true)]
    [InlineData("[]", null, false)]
    public void AmdReadEnvelopeAndRequiredFieldMustBeUsable(string json, string? field, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AmdCpuControlWriter.IsUsableReading(document.RootElement, field));
    }

    [Fact]
    public void OnlyDefinitiveUnsupportedWriteChangesNvidiaAvailabilityAndRedetectClearsIt()
    {
        var writer = new NvidiaGpuControlWriter();
        foreach (var code in new[] { 0, 1, 2, 4, 6, 999 })
        {
            writer.RecordWriteResult("gpu:one", "power", code);
            Assert.False(writer.HasUnsupportedWrite("gpu:one", "power"));
        }
        writer.RecordWriteResult("gpu:one", "power", 3);
        Assert.True(writer.HasUnsupportedWrite("gpu:one", "power"));
        Assert.False(writer.HasUnsupportedWrite("gpu:two", "power"));
        Assert.False(writer.HasUnsupportedWrite("gpu:one", "clock"));
        writer.ResetDetection();
        Assert.False(writer.HasUnsupportedWrite("gpu:one", "power"));
    }
}

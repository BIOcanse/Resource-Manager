using System.Text.Json;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Application.ResourceTable;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationTableTests
{
    [Fact]
    public void ProjectionPreservesPrivatePlusSharedForSoftwareProcessAndSummary()
    {
        ResourceProcessSegment[] processes =
        [
            new(41, "a", null, 12, 0, 0, "12 B") { SharedValue = 4, ProcessStartKey = 100 },
            new(42, "b", null, 16, 0, 0, "16 B") { SharedValue = 6, ProcessStartKey = 120 }
        ];
        var software = new ResourceSoftwareSegment("app", "App", "Other", "Other", 28, 0, 2, processes)
        { SharedValue = 10 };
        var bar = new ResourceBreakdownBar("gpu.1.vram", "GPU1", "B", "capacity", 28, 32, 87.5, [software])
        { SharedValue = 10 };
        var request = new ResourceTableRequest(["gpu.1.vram"], "gpu.1.vram", "desc", "software", new HashSet<string>(), true);
        var snapshot = new ResourceTableProjector().Project(new(DateTimeOffset.UtcNow, [bar]), request);
        Assert.Equal(4, snapshot.Rows.Count);
        Assert.Equal(28, snapshot.Rows[0].Values["gpu.1.vram"].Value);
        Assert.Equal(10, snapshot.Rows[0].Values["gpu.1.vram"].SharedValue);
        Assert.Equal(10, snapshot.Rows[1].Values["gpu.1.vram"].SharedValue);
        Assert.Equal(6, snapshot.Rows[2].Values["gpu.1.vram"].SharedValue);
        Assert.Equal(4, snapshot.Rows[3].Values["gpu.1.vram"].SharedValue);
        var json = JsonSerializer.Serialize(snapshot.Rows[1].Values["gpu.1.vram"], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(10, parsed.RootElement.GetProperty("sharedValue").GetDouble());
        Assert.Equal(28, parsed.RootElement.GetProperty("value").GetDouble());
    }
}

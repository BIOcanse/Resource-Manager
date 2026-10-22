using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareSamplingRejectedDatasetLogTests
{
    [Fact]
    public void RepeatedRejectionIsReportedOnceUntilTheDatasetPublishesAgain()
    {
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 真机上 fan.inventory 与 memory.temperature 出现在不同的到期请求里，每 2–5 秒各被拒绝一次。
        var first = WindowsHardwareMetricSampler.TrackRejectedDatasets(
            rejected,
            Publication(committed: ["cpu.usage"], rejected: ["memory.temperature"]));
        var second = WindowsHardwareMetricSampler.TrackRejectedDatasets(
            rejected,
            Publication(committed: ["storage.inventory"], rejected: ["fan.inventory"]));
        var repeated = new List<string>();
        for (var tick = 0; tick < 10; tick++)
        {
            repeated.AddRange(WindowsHardwareMetricSampler.TrackRejectedDatasets(
                rejected,
                Publication(committed: ["cpu.usage"], rejected: ["MEMORY.TEMPERATURE"])).NewlyRejected);
            repeated.AddRange(WindowsHardwareMetricSampler.TrackRejectedDatasets(
                rejected,
                Publication(committed: ["storage.inventory"], rejected: ["fan.inventory"])).NewlyRejected);
        }

        Assert.Equal(["memory.temperature"], first.NewlyRejected);
        Assert.Empty(first.Recovered);
        Assert.Equal(["fan.inventory"], second.NewlyRejected);
        Assert.Empty(second.Recovered);
        Assert.Empty(repeated);
    }

    [Fact]
    public void RecoveryIsReportedOnceAndALaterRejectionIsReportedAgain()
    {
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _ = WindowsHardwareMetricSampler.TrackRejectedDatasets(
            rejected,
            Publication(committed: [], rejected: ["fan.inventory", "memory.temperature"]));

        var recovered = WindowsHardwareMetricSampler.TrackRejectedDatasets(
            rejected,
            Publication(committed: ["fan.inventory"], rejected: []));
        var stillCommitted = WindowsHardwareMetricSampler.TrackRejectedDatasets(
            rejected,
            Publication(committed: ["fan.inventory"], rejected: []));
        var rejectedAgain = WindowsHardwareMetricSampler.TrackRejectedDatasets(
            rejected,
            Publication(committed: [], rejected: ["fan.inventory"]));

        Assert.Equal(["fan.inventory"], recovered.Recovered);
        Assert.Empty(recovered.NewlyRejected);
        Assert.Empty(stillCommitted.Recovered);
        Assert.Empty(stillCommitted.NewlyRejected);
        Assert.Equal(["fan.inventory"], rejectedAgain.NewlyRejected);
        // memory.temperature 没有出现在后续发布里，保持“被拒绝”状态，不重复也不误报恢复。
        Assert.Contains("memory.temperature", rejected);
    }

    private static HardwareMetricPublicationResult Publication(
        string[] committed,
        string[] rejected)
        => new(rejected.Length == 0 && committed.Length != 0, committed, rejected);
}

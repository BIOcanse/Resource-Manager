using System.Text.Json;
using ResourceManager.App.Domain.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerOptimizationWireContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void HostManagerTypeRename_PreservesSmartStatusAndModeJsonShape()
    {
        var status = JsonSerializer.SerializeToElement(
            new HostManagerSmartCoordinatorStatus(
                "balanced",
                SchedulerRunning: true,
                LastRunAt: null,
                LastRestoreAt: null,
                PendingChangeCount: 2,
                AppliedTargetCount: 3,
                "ready"),
            WebJson);
        Assert.Equal(
            [
                "appliedTargetCount",
                "lastRestoreAt",
                "lastRunAt",
                "message",
                "mode",
                "pendingChangeCount",
                "schedulerRunning"
            ],
            ReadSortedPropertyNames(status));

        var request = JsonSerializer.SerializeToElement(
            new HostManagerSmartCoordinatorModeRequest("balanced"),
            WebJson);
        Assert.Equal(["mode"], ReadSortedPropertyNames(request));
    }

    [Fact]
    public void HostManagerRollbackState_ExposesOnlyPlacementRecoveryAuthority()
    {
        const string existingDocument = """
            {
              "version": 2,
              "lastRunAt": null,
              "lastRestoreAt": null,
              "message": "ordinary",
              "appliedPlacements": [],
              "nativeHostSessionIncarnation": 7
            }
            """;

        var restored = JsonSerializer.Deserialize<HostManagerRollbackStateDocument>(
            existingDocument,
            WebJson);
        Assert.NotNull(restored);
        Assert.Equal(HostManagerRollbackStateDocument.CurrentVersion, restored.Version);
        Assert.Equal<ulong>(7, restored.NativeHostSessionIncarnation);

        var serialized = JsonSerializer.SerializeToElement(restored, WebJson);
        Assert.Equal(
            [
                "appliedPlacements",
                "lastRestoreAt",
                "lastRunAt",
                "message",
                "nativeHostSessionIncarnation",
                "version"
            ],
            ReadSortedPropertyNames(serialized));
    }

    private static string[] ReadSortedPropertyNames(JsonElement element)
        => element.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}

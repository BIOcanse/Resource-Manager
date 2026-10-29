using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.MemoryProtection;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostMemoryProtectionTests
{
    [Fact]
    public async Task OneTreeActionPostsOneNotificationForAllItsRecordsAndRecordsSurviveRestart()
    {
        string root;
        IReadOnlyList<HostMemoryActionRecord> saved;
        await using (var first = await Fixture.StartAsync())
        {
            first.Executor.SliceBytes = 1024;
            var treeRoot = Process(20, "100", 100) with { ExecutablePath = @"C:\fixture\launcher.exe", ParentProcessId = 10 };
            first.Source.Publish(200, 20, [Process(10, "50", 100), treeRoot]);
            await first.CreatePoint();
            using var enabled = await first.Patch(new { enabled = true, minimumProcessGrowthBytes = 1000 });
            await Until(() => first.Source.Active == 1);
            first.Source.Publish(960, 96, [Process(10, "50", 100), treeRoot,
                Process(21, "200", 200) with { ParentProcessId = 20 },
                Process(22, "300", 100) with { ParentProcessId = 21 }, Process(23, "400", 200) with { ParentProcessId = 20 }]);
            await Until(async () => first.ActionNotifications().Length == 1 && (await first.Status()).CurrentActionId is null);
            saved = (await first.Status()).Actions;
            Assert.Equal(4, saved.Count);
            Assert.Single(saved.Select(item => item.ActionId).Distinct());
            var notification = Assert.Single(first.ActionNotifications());
            Assert.Equal(saved[0].ActionId, notification.GetProperty("actionId").GetString());
            Assert.Equal("processTree", notification.GetProperty("targetKind").GetString());
            Assert.Equal(20, notification.GetProperty("processId").GetInt32());
            Assert.Equal("fake-20", notification.GetProperty("name").GetString());
            Assert.Equal(saved.Count, notification.GetProperty("recordCount").GetInt32());
            Assert.Equal(saved.Aggregate(0UL, (sum, item) => sum + item.BytesWritten), notification.GetProperty("bytesWritten").GetUInt64());
            Assert.Equal(4096UL, notification.GetProperty("bytesWritten").GetUInt64());
            Assert.Equal(["actionId", "targetKind", "name", "processId", "recordCount", "bytesWritten"],
                notification.EnumerateObject().Select(property => property.Name));
            // The committed file already holds what the status publishes.
            using var stored = JsonDocument.Parse(File.ReadAllText(first.RecordsPath));
            Assert.Equal(1, stored.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(0, stored.RootElement.GetProperty("unrecordedRecordCount").GetInt32());
            Assert.Equal(saved.Select(item => item.ProcessId),
                stored.RootElement.GetProperty("records").EnumerateArray().Select(item => item.GetProperty("processId").GetInt32()));
            root = first.Root;
            first.KeepRoot = true;
        }
        await using var second = await Fixture.StartAsync(root: root);
        var restored = await second.Status();
        Assert.Equal(JsonSerializer.Serialize(saved, JsonSerializerOptions.Web), JsonSerializer.Serialize(restored.Actions, JsonSerializerOptions.Web));
        Assert.All(restored.Actions, item => Assert.Equal(HostMemoryActionRecordState.History, item.State));
        // The notification itself is also persisted by its owner.
        Assert.Equal(saved[0].ActionId, Assert.Single(second.ActionNotifications()).GetProperty("actionId").GetString());
        var list = (await second.Client.GetFromJsonAsync<JsonElement>("/api/memory-protection"));
        Assert.Equal(4, list.GetProperty("actions").GetArrayLength());
        Assert.Equal(4096, list.GetProperty("recordCapacity").GetInt32());
    }

    [Fact]
    public async Task CorruptRecordFileIsLeftInPlaceAndProtectionStartsWithNoRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-memory-protection-host-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "UserData", "MemoryProtection", "action-records.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string corrupt = "{\"version\":1,\"unrecordedRecordCount\":-1,\"records\":[]}";
        File.WriteAllText(path, corrupt);
        await using var second = await Fixture.StartAsync(root: root);
        var status = await second.Status();
        Assert.Empty(status.Actions);
        Assert.Equal(0, status.UnrecordedRecordCount);
        Assert.Equal(corrupt, File.ReadAllText(path));
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Notifications;

namespace Resource_Manager_APP.Tests;

public sealed class NotificationOwnerTests
{
    private static JsonElement Update(string version) => NotificationFacts.Of(new UpdateAvailableNotificationFacts(version));

    [Fact]
    public void SequenceStartsAtOneIsPersistedAndContinuesAfterRestart()
    {
        using var files = new Files();
        var owner = NotificationTestSupport.Create(files.Root, persist: true);
        Assert.Empty(owner.Read().Notifications);
        var first = owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.0"));
        var second = owner.Post(NotificationKinds.AccessRequest.Name,
            NotificationFacts.Of(new AccessRequestNotificationFacts(new string('a', 64), "tool", @"C:\Tools\tool.exe")));
        Assert.Equal([1L, 2L], owner.Read().Notifications.Select(item => item.Sequence));
        Assert.Equal(TimeSpan.Zero, first.OccurredAt.Offset);
        using (var stored = JsonDocument.Parse(File.ReadAllText(files.Path)))
        {
            Assert.Equal(1, stored.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(3, stored.RootElement.GetProperty("nextSequence").GetInt64());
            Assert.Equal(2, stored.RootElement.GetProperty("notifications").GetArrayLength());
        }

        var restarted = NotificationTestSupport.Create(files.Root, persist: true);
        var restored = restarted.Read().Notifications;
        Assert.Equal([1L, 2L], restored.Select(item => item.Sequence));
        Assert.Equal(second.Kind, restored[1].Kind);
        Assert.True(JsonElement.DeepEquals(second.Facts, restored[1].Facts));
        Assert.Equal(second.OccurredAt, restored[1].OccurredAt);
        Assert.Equal(3, restarted.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.1")).Sequence);
    }

    [Fact]
    public void RetainedCountEvictsOldestButNeverReusesSequence()
    {
        using var files = new Files();
        var owner = NotificationTestSupport.Create(files.Root, retainedCount: 2, persist: true);
        for (var index = 0; index < 3; index++) owner.Post(NotificationKinds.UpdateAvailable.Name, Update($"0.3.{index}"));
        Assert.Equal([2L, 3L], owner.Read().Notifications.Select(item => item.Sequence));
        var restarted = NotificationTestSupport.Create(files.Root, retainedCount: 2, persist: true);
        Assert.Equal([2L, 3L], restarted.Read().Notifications.Select(item => item.Sequence));
        Assert.Equal(4, restarted.Post(NotificationKinds.UpdateAvailable.Name, Update("0.4.0")).Sequence);
        Assert.Equal([3L, 4L], restarted.Read().Notifications.Select(item => item.Sequence));
    }

    [Fact]
    public async Task SubscriptionGivesCurrentListFirstAndEachChangeIsWrittenBeforeItIsPublished()
    {
        using var files = new Files();
        var owner = NotificationTestSupport.Create(files.Root, persist: true);
        owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.0"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = owner.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, Assert.Single(reader.Current.Notifications).Sequence);
        var next = reader.MoveNextAsync().AsTask();
        owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.1"));
        Assert.True(await next);
        Assert.Equal([1L, 2L], reader.Current.Notifications.Select(item => item.Sequence));
        // The published list is already the committed file.
        using var stored = JsonDocument.Parse(File.ReadAllText(files.Path));
        Assert.Equal([1L, 2L], stored.RootElement.GetProperty("notifications").EnumerateArray()
            .Select(item => item.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    public void FailedWriteStillPublishesFromMemoryAndLogs()
    {
        using var files = new Files();
        var logger = new CountingLogger();
        var owner = NotificationTestSupport.Create(files.Root, persist: true, logger: logger);
        owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.0"));
        var contents = File.ReadAllText(files.Path);
        File.SetAttributes(files.Path, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(2, owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.1")).Sequence);
            Assert.Equal([1L, 2L], owner.Read().Notifications.Select(item => item.Sequence));
            Assert.Equal(1, logger.Errors);
            Assert.Equal(contents, File.ReadAllText(files.Path));
        }
        finally { File.SetAttributes(files.Path, FileAttributes.Normal); }
        Assert.Equal(3, owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.2")).Sequence);
        using var stored = JsonDocument.Parse(File.ReadAllText(files.Path));
        Assert.Equal(3, stored.RootElement.GetProperty("notifications").GetArrayLength());
    }

    [Fact]
    public void HostWithoutPersistenceKeepsNotificationsInMemoryOnly()
    {
        using var files = new Files();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Path)!);
        File.WriteAllText(files.Path, "existing file must remain untouched");
        var owner = NotificationTestSupport.Create(files.Root, persist: false);
        Assert.Empty(owner.Read().Notifications);
        Assert.Equal(1, owner.Post(NotificationKinds.UpdateAvailable.Name, Update("0.3.0")).Sequence);
        Assert.Equal("existing file must remain untouched", File.ReadAllText(files.Path));
        Assert.Empty(NotificationTestSupport.Create(files.Root, persist: false).Read().Notifications);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":2,\"nextSequence\":1,\"notifications\":[]}")]
    [InlineData("{\"version\":1,\"nextSequence\":1,\"notifications\":[{\"sequence\":1,\"kind\":\"update-available\",\"occurredAt\":\"2026-10-29T00:00:00+00:00\",\"facts\":{}}]}")]
    [InlineData("{\"version\":1,\"nextSequence\":3,\"notifications\":[{\"sequence\":1,\"kind\":\"update-available\",\"occurredAt\":\"2026-10-29T00:00:00+00:00\",\"facts\":1}]}")]
    public void CorruptFileIsLoggedLeftInPlaceAndOwnerStartsEmpty(string contents)
    {
        using var files = new Files();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Path)!);
        File.WriteAllText(files.Path, contents);
        var logger = new CountingLogger();
        var owner = NotificationTestSupport.Create(files.Root, persist: true, logger: logger);
        Assert.Empty(owner.Read().Notifications);
        Assert.Equal(1, logger.Errors);
        Assert.Equal(contents, File.ReadAllText(files.Path));
    }

    [Fact]
    public void OwnerRejectsUnregisteredKindsAndFactsThatAreNotObjects()
    {
        var owner = NotificationTestSupport.Create();
        Assert.Throws<ArgumentException>(() => owner.Post("unknown-kind", Update("0.3.0")));
        using var array = JsonDocument.Parse("[]");
        Assert.Throws<ArgumentException>(() => owner.Post(NotificationKinds.UpdateAvailable.Name, array.RootElement));
        Assert.Empty(owner.Read().Notifications);
    }

    [Fact]
    public void FreedomPointsCompileDefaultsAndRejectOutOfRangeValues()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(64, plan.Notifications!.RetainedCount);
        Assert.Equal(4096, plan.HostMemoryProtectionRecords!.RecordCapacity);
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            FreedomPointTestFactory.Point(root, "resource-manager/backend/monitoring/build/complex/1")["value"]!["retained_count"] = 1024;
            FreedomPointTestFactory.Point(root, "resource-manager/backend/monitoring/build/complex/2")["value"]!["record_capacity"] = 16384;
        });
        Assert.Equal(1024, changed.Notifications!.RetainedCount);
        Assert.Equal(16384, changed.HostMemoryProtectionRecords!.RecordCapacity);
        Assert.NotEqual(plan.BuildSha256, changed.BuildSha256);
        Assert.Throws<InvalidDataException>(() => new NotificationOptions(0).Validate());
        Assert.Throws<InvalidDataException>(() => new NotificationOptions(1025).Validate());
        Assert.Equal(1, new NotificationOptions(1).Validate().RetainedCount);
        Assert.Throws<InvalidDataException>(() => new HostMemoryProtectionRecordOptions(0).Validate());
        Assert.Throws<InvalidDataException>(() => new HostMemoryProtectionRecordOptions(16385).Validate());
        Assert.Equal(1, new HostMemoryProtectionRecordOptions(1).Validate().RecordCapacity);
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rm-notifications-{Guid.NewGuid():N}");
        internal string Path => NotificationTestSupport.StorePath(Root);
        public void Dispose()
        {
            if (File.Exists(Path)) File.SetAttributes(Path, FileAttributes.Normal);
            WindowsNativeAtomicFileCommitter.DeleteExact(Path);
            foreach (var directory in new[] { System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.Combine(Root, "UserData"), Root })
                if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private sealed class CountingLogger : ILogger<NotificationOwner>
    {
        internal int Errors;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Error) Interlocked.Increment(ref Errors); }
    }
}

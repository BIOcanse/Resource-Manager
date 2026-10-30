using System.Text.Json;
using ResourceManager.NativeUi.Configuration;
using ResourceManager.NativeUi.Preferences;
using ResourceManager.NativeUi.SystemIntegration;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsFileObserverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-app-settings-observer-" + Guid.NewGuid().ToString("N"));
    private readonly List<Action> queued = [];
    private readonly object queueGate = new();
    private string Settings => Path.Combine(root, "app-settings.json");

    public AppSettingsFileObserverTests() => Directory.CreateDirectory(root);

    [Fact]
    public void UnchangedFileIsNotReadAgain()
    {
        File.WriteAllText(Settings, Enabled(true));
        var applied = new List<bool>();
        using var observer = Observer();
        observer.Subscribe(read => applied.Add(TaskManagerShortcutReplacementController.ReadEnabled(read)));
        observer.Start();

        observer.RequestRead();
        Drain();
        observer.RequestRead();
        Drain();

        Assert.Equal(1, observer.ReadCount);
        Assert.Equal(new[] { true }, applied);
    }

    [Fact]
    public void ChangedFileIsReadOnceForEveryConsumer()
    {
        File.WriteAllText(Settings, Enabled(false));
        var first = new List<bool>();
        var second = new List<bool>();
        using var observer = Observer();
        observer.Subscribe(read => first.Add(TaskManagerShortcutReplacementController.ReadEnabled(read)));
        observer.Subscribe(read => second.Add(TaskManagerShortcutReplacementController.ReadEnabled(read)));
        observer.Start();

        File.WriteAllText(Settings, Enabled(true) + "  ");
        observer.RequestRead();
        Drain();

        Assert.Equal(2, observer.ReadCount);
        Assert.Equal(new[] { false, true }, first);
        Assert.Equal(new[] { false, true }, second);
    }

    [Fact]
    public void EventsBeforeTheQueuedReadMergeIntoOneRead()
    {
        File.WriteAllText(Settings, Enabled(false));
        var applied = new List<bool>();
        using var observer = Observer();
        observer.Subscribe(read => applied.Add(TaskManagerShortcutReplacementController.ReadEnabled(read)));
        observer.Start();

        File.WriteAllText(Settings, Enabled(true) + " ");
        observer.RequestRead();
        observer.RequestRead();
        observer.RequestRead();
        lock (queueGate) Assert.Single(queued);
        Drain();

        Assert.Equal(2, observer.ReadCount);
        Assert.Equal(new[] { false, true }, applied);
    }

    [Fact]
    public async Task OneAtomicSaveSeenByTheWatcherIsReadOnce()
    {
        File.WriteAllText(Settings, Enabled(false));
        var applied = new List<bool>();
        using var observer = Observer();
        observer.Subscribe(read => applied.Add(TaskManagerShortcutReplacementController.ReadEnabled(read)));
        observer.Start();

        // The backend's save: a temporary file in the same directory renamed over the settings file.
        var temporary = $"{Settings}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, Enabled(true) + "   ");
        File.Move(temporary, Settings, overwrite: true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (queueGate) if (queued.Count != 0) break;
            await Task.Delay(20);
        }
        await Task.Delay(200); // Let every event of this save arrive before draining.
        Drain();
        await Task.Delay(200);
        Drain();

        Assert.Equal(2, observer.ReadCount);
        Assert.Equal(new[] { false, true }, applied);
    }

    [Fact]
    public void ExplicitReloadReadsEvenWhenTheFileVersionIsUnchanged()
    {
        File.WriteAllText(Settings, Enabled(true));
        using var observer = Observer();
        observer.Start();

        observer.RequestRead(force: true);
        Drain();

        Assert.Equal(2, observer.ReadCount);
    }

    [Fact]
    public void MissingAndUnreadableFilesKeepTheirPreviousMeaning()
    {
        using var observer = Observer();
        var reads = new List<AppSettingsFileReadState>();
        observer.Subscribe(read => reads.Add(read.State));
        observer.Start();
        Assert.False(TaskManagerShortcutReplacementController.ReadEnabled(AppSettingsFileRead.Missing));
        Assert.True(ForceTerminateHotkeyController.TryReadSettings(AppSettingsFileRead.Missing, out var enabled, out var definition));
        Assert.False(enabled);
        Assert.True(definition.IsEmpty);
        Assert.False(ForceTerminateHotkeyController.TryReadSettings(AppSettingsFileRead.Unreadable, out _, out _));

        File.WriteAllText(Settings, "{broken");
        observer.RequestRead();
        Drain();

        // An unreadable file is read again on the next event, so a late watcher event may add one more.
        Assert.Equal(AppSettingsFileReadState.Missing, reads[0]);
        Assert.NotEmpty(reads.Skip(1));
        Assert.All(reads.Skip(1), state => Assert.Equal(AppSettingsFileReadState.Unreadable, state));
    }

    [Fact]
    public void FrontendPreferencesAreReadFromDiskOnceAndFollowThisOwnersSaves()
    {
        var cache = Path.Combine(root, "frontend-preferences.cache.json");
        var owner = new FrontendPreferencesOwner(cache);
        owner.Save(new() { Appearance = new() { Theme = "dark" } });
        var revision = owner.Revision;

        // Only this owner writes the cache; a foreign write is not read back.
        File.WriteAllText(cache, JsonSerializer.Serialize(new FrontendPreferences { Appearance = new() { Theme = "light" } },
            FrontendPreferencesOwner.Json));
        Assert.Equal("dark", owner.Read().Appearance.Theme);
        Assert.Equal(revision, owner.Revision);

        owner.SaveGlobal(owner.Read() with { Appearance = new() { Theme = "lowContrast" } });
        Assert.Equal("lowContrast", owner.Read().Appearance.Theme);
        Assert.Equal(revision + 1, owner.Revision);
        Assert.Equal("lowContrast", new FrontendPreferencesOwner(cache).Read().Appearance.Theme);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }

    private AppSettingsFileObserver Observer() => new(Settings, action =>
    {
        lock (queueGate) queued.Add(action);
    });

    private void Drain()
    {
        while (true)
        {
            Action[] actions;
            lock (queueGate)
            {
                actions = [.. queued];
                queued.Clear();
            }
            if (actions.Length == 0) return;
            foreach (var action in actions) action();
        }
    }

    private static string Enabled(bool enabled) =>
        $$$"""{"systemIntegration":{"taskManagerShortcutReplacementEnabled":{{{(enabled ? "true" : "false")}}}}}""";
}

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

// The tests replace global trace listeners, so they do not run in parallel with each other.
[CollectionDefinition("Tray backend notifications", DisableParallelization = true)]
public sealed class TrayBackendNotificationsCollection { }

[Collection("Tray backend notifications")]
public sealed class TrayBackendNotificationsTests : IDisposable
{
    private const string Memory = TrayBackendNotifications.MemoryProtectionKind;
    private const string Update = TrayBackendNotifications.UpdateAvailableKind;
    private const string Access = TrayBackendNotifications.AccessRequestKind;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-tray-backend-notifications-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Title, string Message)> balloons = [];
    private Action? afterBalloon;
    private string Cache => Path.Combine(root, "frontend-preferences.json");
    private string StatePath => Cache + ".native-notifications.json";
    private FrontendPreferencesOwner Owner() => new(Cache);

    // A new consumer over a new owner of the same files is what a tray restart creates.
    private TrayBackendNotifications Consumer(string language = "en-US") =>
        new(Owner(), (title, message) => { balloons.Add((title, message)); afterBalloon?.Invoke(); }, () => language);

    public TrayBackendNotificationsTests() => Directory.CreateDirectory(root);

    [Fact]
    public void FirstListOnlyRecordsItsLatestSequence()
    {
        Consumer().ObserveFrame(Frame((4, Update, UpdateFacts("1.0.0")), (7, Access, AccessFacts())));
        Assert.Empty(balloons);
        Assert.Equal(7, Owner().ReadLastHandledNotificationSequence());
        Consumer().ObserveFrame(Frame((4, Update, UpdateFacts("1.0.0")), (7, Access, AccessFacts()), (8, Update, UpdateFacts("1.1.0"))));
        var shown = Assert.Single(balloons);
        Assert.Contains("1.1.0", shown.Message);
        Assert.Equal(8, Owner().ReadLastHandledNotificationSequence());
    }

    [Fact]
    public void EmptyFirstListRecordsZeroSoSequenceOneLaterShows()
    {
        Assert.Null(Owner().ReadLastHandledNotificationSequence());
        Consumer().ObserveFrame(Frame());
        Assert.Equal(0, Owner().ReadLastHandledNotificationSequence());
        Assert.Empty(balloons);
        Consumer().ObserveFrame(Frame((1, Update, UpdateFacts("1.0.0"))));
        Assert.Single(balloons);
        Assert.Equal(1, Owner().ReadLastHandledNotificationSequence());
    }

    [Fact]
    public void SameNotificationIsNotShownTwiceAlsoAfterRestart()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        var consumer = Consumer();
        var frame = Frame((1, Update, UpdateFacts("1.0.0")), (2, Memory, ProcessFacts()));
        consumer.ObserveFrame(frame);
        Assert.Equal(2, balloons.Count);
        consumer.ObserveFrame(frame);
        Consumer().ObserveFrame(frame); // Restart: a new owner instance over the same files.
        Assert.Equal(2, balloons.Count);
        Consumer().ObserveFrame(Frame((2, Memory, ProcessFacts()), (3, Access, AccessFacts())));
        Assert.Equal(3, balloons.Count);
        Assert.Equal(3, Owner().ReadLastHandledNotificationSequence());
    }

    [Fact]
    public void RecordIsStoredAfterEachNotificationSoAFailedShowDoesNotRepeatEarlierOnes()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        var calls = 0;
        var consumer = new TrayBackendNotifications(Owner(), (title, message) =>
        {
            if (++calls == 2) throw new InvalidOperationException("balloon failed");
            balloons.Add((title, message));
        }, () => "en-US");
        var frame = Frame((1, Update, UpdateFacts("1.0.0")), (2, Update, UpdateFacts("1.1.0")), (3, Update, UpdateFacts("1.2.0")));
        Assert.Throws<InvalidOperationException>(() => consumer.ObserveFrame(frame));
        Assert.Equal(1, Owner().ReadLastHandledNotificationSequence());
        consumer.ObserveFrame(frame);
        Assert.Equal(new[] { "1.0.0", "1.1.0", "1.2.0" }, balloons.Select(item => item.Message.Split(' ')[1]));
        Assert.Equal(3, Owner().ReadLastHandledNotificationSequence());
    }

    [Fact]
    public void LatestBelowRecordResetsRecordToZeroAndTheListShows()
    {
        Owner().RecordLastHandledNotificationSequence(40);
        Consumer().ObserveFrame(Frame((1, Update, UpdateFacts("2.0.0")), (2, Access, AccessFacts())));
        Assert.Equal(2, balloons.Count);
        Assert.Equal(2, Owner().ReadLastHandledNotificationSequence());

        Owner().RecordLastHandledNotificationSequence(40);
        Consumer().ObserveFrame(Frame());
        Assert.Equal(0, Owner().ReadLastHandledNotificationSequence());
        Assert.Equal(2, balloons.Count);

        // Equal to the record is not a rebuild and shows nothing.
        Owner().RecordLastHandledNotificationSequence(2);
        Consumer().ObserveFrame(Frame((1, Update, UpdateFacts("2.0.0")), (2, Access, AccessFacts())));
        Assert.Equal(2, balloons.Count);
        Assert.Equal(2, Owner().ReadLastHandledNotificationSequence());
    }

    // The frontend preference names of each kind's three switches (docs/notifications_2026-10-29.md, 设置在前端).
    private static (string Notify, string System, string InApp) Switches(string kind) => kind switch
    {
        Memory => (nameof(FrontendNotifications.MemoryProtectionNotify), nameof(FrontendNotifications.MemoryProtectionSystem),
            nameof(FrontendNotifications.MemoryProtectionInApp)),
        Update => (nameof(FrontendNotifications.UpdateNotify), nameof(FrontendNotifications.UpdateSystem),
            nameof(FrontendNotifications.UpdateInApp)),
        _ => (nameof(FrontendNotifications.AccessRequestNotify), nameof(FrontendNotifications.AccessRequestSystem),
            nameof(FrontendNotifications.AccessRequestInApp))
    };

    [Theory]
    [InlineData(Memory)]
    [InlineData(Update)]
    [InlineData(Access)]
    public void MasterOffOnlyAdvancesTheRecord(string kind)
    {
        var (notify, _, _) = Switches(kind);
        Owner().RecordLastHandledNotificationSequence(0);
        Set(notify, "off");
        Consumer().ObserveFrame(Frame((1, kind, FactsFor(kind))));
        Assert.Empty(balloons);
        Assert.Equal(1, Owner().ReadLastHandledNotificationSequence());
        Set(notify, "on");
        Consumer().ObserveFrame(Frame((1, kind, FactsFor(kind))));
        Assert.Empty(balloons); // A notification skipped while off is not shown later.
        Consumer().ObserveFrame(Frame((1, kind, FactsFor(kind)), (2, kind, FactsFor(kind))));
        Assert.Single(balloons);
    }

    [Theory]
    [InlineData(Memory)]
    [InlineData(Update)]
    [InlineData(Access)]
    public void WindowsChannelOffOnlyAdvancesTheRecord(string kind)
    {
        var (_, system, _) = Switches(kind);
        Owner().RecordLastHandledNotificationSequence(0);
        Set(system, "off");
        Consumer().ObserveFrame(Frame((1, kind, FactsFor(kind)), (2, kind, FactsFor(kind))));
        Assert.Empty(balloons);
        Assert.Equal(2, Owner().ReadLastHandledNotificationSequence());
    }

    [Theory]
    [InlineData(Memory)]
    [InlineData(Update)]
    [InlineData(Access)]
    public void SwitchesAreReadAtTheMomentOfEachDecision(string kind)
    {
        var (notify, system, _) = Switches(kind);
        Owner().RecordLastHandledNotificationSequence(0);
        var consumer = Consumer();
        // Turning a switch off after the first balloon stops the next one in the same frame.
        afterBalloon = () => Set(system, "off");
        consumer.ObserveFrame(Frame((1, kind, FactsFor(kind)), (2, kind, FactsFor(kind))));
        Assert.Single(balloons);
        Assert.Equal(2, Owner().ReadLastHandledNotificationSequence());
        afterBalloon = null;
        Set(system, "on");
        Set(notify, "off");
        consumer.ObserveFrame(Frame((3, kind, FactsFor(kind))));
        Assert.Single(balloons);
        Set(notify, "on");
        consumer.ObserveFrame(Frame((4, kind, FactsFor(kind))));
        Assert.Equal(2, balloons.Count);
    }

    [Theory]
    [InlineData(Memory)]
    [InlineData(Update)]
    [InlineData(Access)]
    public void InAppSwitchHasNoEffect(string kind)
    {
        var (_, system, inApp) = Switches(kind);
        Owner().RecordLastHandledNotificationSequence(0);
        Set(inApp, "off");
        Consumer().ObserveFrame(Frame((1, kind, FactsFor(kind))));
        Assert.Single(balloons);
        Set(inApp, "on");
        Set(system, "off");
        Consumer().ObserveFrame(Frame((2, kind, FactsFor(kind))));
        Assert.Single(balloons);
    }

    [Fact]
    public void OtherKindsSwitchesDoNotAffectAKind()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        Set(nameof(FrontendNotifications.UpdateNotify), "off");
        Set(nameof(FrontendNotifications.AccessRequestSystem), "off");
        Consumer().ObserveFrame(Frame((1, Memory, ProcessFacts()), (2, Update, UpdateFacts("1.0.0")), (3, Access, AccessFacts())));
        var shown = Assert.Single(balloons);
        Assert.Equal(NativeTextCatalog.For("en-US").MemoryProtectionTitle, shown.Title);
    }

    [Fact]
    public void UnknownKindsAndUndecodableFactsAreSkippedAndLaterNotificationsStillShow()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        using var log = new CaptureTrace();
        Trace.Listeners.Add(log);
        try
        {
            Consumer().ObserveFrame(Frame(
                (1, "report.something-new", new { anything = 1 }),
                (2, Memory, new { targetKind = "service", name = "x", processId = 1, recordCount = 1, bytesWritten = 1 }),
                (3, Memory, new { targetKind = "process", name = "x", processId = 1, recordCount = 1, bytesWritten = -1 }),
                (4, Memory, new { targetKind = "processTree", name = "x", processId = 1, bytesWritten = 1 }),
                (5, Update, new { version = "" }),
                (6, Access, new { name = "Agent" }),
                (7, Update, "not an object"),
                (8, Memory, ProcessFacts()),
                (9, Update, UpdateFacts("1.0.0")),
                (10, Access, AccessFacts())));
        }
        finally { Trace.Listeners.Remove(log); }
        Assert.Equal(3, balloons.Count);
        Assert.Equal(10, Owner().ReadLastHandledNotificationSequence());
        var warnings = log.Lines.Where(line => line.Contains("do not decode", StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, warnings.Length);
        foreach (var (sequence, kind) in new[] { (2, Memory), (3, Memory), (4, Memory), (5, Update), (6, Access), (7, Update) })
            Assert.Single(warnings, line => line.Contains($"notification {sequence} of kind {kind}", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains("report.something-new", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessTargetDoesNotRequireRecordCount()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        Consumer().ObserveFrame(Frame((1, Memory, new { targetKind = "process", name = "game.exe", processId = 4242, bytesWritten = 0 })));
        var shown = Assert.Single(balloons);
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo("en-US"),
            NativeTextCatalog.For("en-US").MemoryProtectionProcessMessageFormat, "game.exe", 4242, "0 B"), shown.Message);
    }

    [Fact]
    public void FramesWithoutANotificationListDoNotMoveTheRecord()
    {
        var consumer = Consumer();
        foreach (var value in new[]
        {
            """{"statusCode":400,"error":"unknown-subscription-path"}""", """[]""", """null""", """{"notifications":null}""",
            """{"notifications":{}}"""
        })
            consumer.ObserveFrame(JsonDocument.Parse(value).RootElement.Clone());
        Assert.Null(Owner().ReadLastHandledNotificationSequence());
        Assert.False(File.Exists(StatePath));
        Owner().RecordLastHandledNotificationSequence(5);
        consumer.ObserveFrame(JsonDocument.Parse("""{"statusCode":400,"error":"rejected"}""").RootElement.Clone());
        Assert.Equal(5, Owner().ReadLastHandledNotificationSequence());
        Assert.Empty(balloons);
    }

    [Fact]
    public void ListWithAnInvalidSequenceOrKindIsIgnoredWithoutMovingTheRecord()
    {
        Owner().RecordLastHandledNotificationSequence(1);
        using var log = new CaptureTrace();
        Trace.Listeners.Add(log);
        try
        {
            foreach (var value in new[]
            {
                """{"notifications":[{"sequence":3,"kind":"update-available","facts":{"version":"1"}},{"sequence":2,"kind":"update-available","facts":{"version":"2"}}]}""",
                """{"notifications":[{"sequence":0,"kind":"update-available","facts":{"version":"1"}}]}""",
                """{"notifications":[{"sequence":"3","kind":"update-available","facts":{"version":"1"}}]}""",
                """{"notifications":[{"sequence":3,"facts":{"version":"1"}}]}"""
            })
                Consumer().ObserveFrame(JsonDocument.Parse(value).RootElement.Clone());
        }
        finally { Trace.Listeners.Remove(log); }
        Assert.Empty(balloons);
        Assert.Equal(1, Owner().ReadLastHandledNotificationSequence());
        Assert.Equal(4, log.Lines.Count(line => line.Contains("frame is ignored", StringComparison.Ordinal)));
    }

    [Fact]
    public void EveryShellLanguageFormatsEveryMessageWithItsValues()
    {
        const string processName = "game{1}.exe";
        const string rootName = "launcher.exe";
        const string version = "3.4.5-beta.2";
        const string requester = "Self <b>{0}</b>";
        const long processBytes = 1536;
        const long treeBytes = 3L * 1024 * 1024 * 1024 + 512L * 1024 * 1024;
        foreach (var language in NativeLanguage.SupportedIds)
        {
            Directory.CreateDirectory(root);
            balloons.Clear();
            Owner().RecordLastHandledNotificationSequence(0);
            Consumer(language).ObserveFrame(Frame(
                (1, Memory, new { actionId = "a", targetKind = "process", name = processName, processId = 4242, recordCount = 1, bytesWritten = processBytes }),
                (2, Memory, new { actionId = "b", targetKind = "processTree", name = rootName, processId = 917, recordCount = 23, bytesWritten = treeBytes }),
                (3, Update, UpdateFacts(version)),
                (4, Access, new { requestId = "r", name = requester, observedExecutablePath = @"C:\Untrusted\Folder\request{2}.exe" })));
            var text = NativeTextCatalog.For(language);
            var culture = TrayBackendNotifications.ShellCulture(language);
            Assert.Equal(4, balloons.Count);
            Assert.Equal(text.MemoryProtectionTitle, balloons[0].Title);
            Assert.Equal(text.MemoryProtectionTitle, balloons[1].Title);
            Assert.Equal(text.UpdateAvailableTitle, balloons[2].Title);
            Assert.Equal(text.AccessRequestTitle, balloons[3].Title);
            var processSize = TrayBackendNotifications.FormatDumpSize(processBytes, culture);
            var treeSize = TrayBackendNotifications.FormatDumpSize(treeBytes, culture);
            Assert.Equal(string.Format(culture, text.MemoryProtectionProcessMessageFormat, processName, 4242, processSize), balloons[0].Message);
            Assert.Equal(string.Format(culture, text.MemoryProtectionTreeMessageFormat, rootName, 917, 23, treeSize), balloons[1].Message);
            foreach (var value in new[] { processName, "4242", processSize }) Assert.Contains(value, balloons[0].Message);
            foreach (var value in new[] { rootName, "917", "23", treeSize }) Assert.Contains(value, balloons[1].Message);
            Assert.Contains(version, balloons[2].Message);
            Assert.Contains(requester, balloons[3].Message);
            Assert.Contains("request{2}.exe", balloons[3].Message);
            Assert.DoesNotContain(@"C:\Untrusted", balloons[3].Message);
            if (language != "en-US")
            {
                var english = NativeTextCatalog.For("en-US");
                Assert.NotEqual(english.MemoryProtectionTitle, text.MemoryProtectionTitle);
                Assert.NotEqual(english.MemoryProtectionProcessMessageFormat, text.MemoryProtectionProcessMessageFormat);
                Assert.NotEqual(english.MemoryProtectionTreeMessageFormat, text.MemoryProtectionTreeMessageFormat);
            }
            File.Delete(StatePath);
        }
    }

    [Fact]
    public void DumpSizeUsesBinaryUnitsWithOneDecimalFromKibUp()
    {
        var invariant = CultureInfo.InvariantCulture;
        Assert.Equal("0 B", TrayBackendNotifications.FormatDumpSize(0, invariant));
        Assert.Equal("1023 B", TrayBackendNotifications.FormatDumpSize(1023, invariant));
        Assert.Equal("1.0 KiB", TrayBackendNotifications.FormatDumpSize(1024, invariant));
        Assert.Equal("1.5 KiB", TrayBackendNotifications.FormatDumpSize(1536, invariant));
        Assert.Equal("1.0 MiB", TrayBackendNotifications.FormatDumpSize(1024 * 1024, invariant));
        Assert.Equal("2.5 MiB", TrayBackendNotifications.FormatDumpSize(5 * 512 * 1024, invariant));
        Assert.Equal("5.0 GiB", TrayBackendNotifications.FormatDumpSize(5L * 1024 * 1024 * 1024, invariant));
        Assert.Equal("2048.0 GiB", TrayBackendNotifications.FormatDumpSize(2L * 1024 * 1024 * 1024 * 1024, invariant));
        Assert.Equal("1,5 KiB", TrayBackendNotifications.FormatDumpSize(1536, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(CultureInfo.GetCultureInfo("de-DE"), TrayBackendNotifications.ShellCulture("de-DE"));
    }

    [Fact]
    public void OldStateFieldsReadAsNoRecordAndTheNextWriteDropsThem()
    {
        File.WriteAllText(StatePath, """{"updateSystemNotifiedVersion":"1.2.0","accessRequestNotifiedIds":["first","second"]}""");
        Assert.Null(Owner().ReadLastHandledNotificationSequence());
        Consumer().ObserveFrame(Frame((5, Update, UpdateFacts("1.2.0")), (6, Access, AccessFacts())));
        Assert.Empty(balloons); // After the upgrade the first list only records, as on a fresh install.
        var written = File.ReadAllText(StatePath);
        Assert.DoesNotContain("updateSystemNotifiedVersion", written);
        Assert.DoesNotContain("accessRequestNotifiedIds", written);
        using (var state = JsonDocument.Parse(written))
            Assert.Equal(6, state.RootElement.GetProperty("lastHandledSequence").GetInt64());
        File.WriteAllText(StatePath, """{"lastHandledSequence":-1}""");
        Assert.Throws<InvalidDataException>(() => Owner().ReadLastHandledNotificationSequence());
        Assert.Throws<InvalidDataException>(() => Owner().RecordLastHandledNotificationSequence(-1));
        File.WriteAllText(StatePath, """{"lastHandledSequence":null}""");
        Assert.Null(Owner().ReadLastHandledNotificationSequence());
    }

    [Fact]
    public void FrontendSavesAndResetDoNotEraseTheNativeRecord()
    {
        Owner().RecordLastHandledNotificationSequence(9);
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with { Notifications = owner.Read().Notifications with { UpdateSystem = "off" } });
        Assert.Equal(9, Owner().ReadLastHandledNotificationSequence());
        owner.Reset();
        Assert.Equal(9, Owner().ReadLastHandledNotificationSequence());
    }

    [Fact]
    public async Task ReconnectReceivesTheWholeListAgainWithoutRepeatingAndBacksOff()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        using var cancel = new CancellationTokenSource();
        var attempts = 0;
        var delays = new List<int>();
        await Consumer().RunAsync(_ =>
        {
            attempts++;
            return Task.FromResult(Response(new
            {
                subscriptionId = TrayBackendNotifications.SubscriptionId,
                value = FrameObject((1, Update, UpdateFacts("1.0.0")), (2, Memory, TreeFacts()))
            }, new { subscriptionId = "other", value = FrameObject((3, Update, UpdateFacts("9.9.9"))) }));
        }, action => action(), cancel.Token, (duration, _) =>
        {
            delays.Add((int)duration.TotalSeconds);
            if (delays.Count == 7) cancel.Cancel();
            return Task.CompletedTask;
        });
        Assert.Equal(7, attempts);
        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30, 30 }, delays);
        Assert.Equal(2, balloons.Count);
        Assert.Equal(2, Owner().ReadLastHandledNotificationSequence());
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(403, 0)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(401, 2)]
    [InlineData(403, 2)]
    public async Task DeniedStreamBacksOffWithoutBalloonsRecordChangesOrLogSpam(int status, int mode)
    {
        Owner().RecordLastHandledNotificationSequence(3);
        using var cancel = new CancellationTokenSource();
        using var log = new CaptureTrace();
        Trace.Listeners.Add(log);
        var delays = new List<int>();
        var attempts = 0;
        try
        {
            await Consumer().RunAsync(_ =>
            {
                attempts++;
                if (mode == 2) return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Denied by session.", null, (HttpStatusCode)status));
                return Task.FromResult(mode == 1
                    ? Response(new { subscriptionId = TrayBackendNotifications.SubscriptionId,
                        value = new { statusCode = status, error = "access-scope-required" } })
                    : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("not a list") });
            }, action => action(), cancel.Token, (duration, _) =>
            {
                delays.Add((int)duration.TotalSeconds);
                if (delays.Count == 7) cancel.Cancel();
                return Task.CompletedTask;
            });
        }
        finally { Trace.Listeners.Remove(log); }
        Assert.Equal(7, attempts);
        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30, 30 }, delays);
        Assert.Empty(balloons);
        Assert.Equal(3, Owner().ReadLastHandledNotificationSequence());
        Assert.DoesNotContain(log.Lines, line => line.Contains("Tray notification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancelledGenerationCannotShowAQueuedFrame()
    {
        Owner().RecordLastHandledNotificationSequence(0);
        using var cancel = new CancellationTokenSource();
        var pending = new Queue<Action>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Consumer().RunAsync(_ => Task.FromResult(Response(new
            {
                subscriptionId = TrayBackendNotifications.SubscriptionId,
                value = FrameObject((1, Update, UpdateFacts("1.0.0")))
            })), pending.Enqueue, cancel.Token,
            async (_, token) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        pending.Dequeue()();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(balloons);
        Assert.Equal(0, Owner().ReadLastHandledNotificationSequence());
    }

    private void Set(string property, string value)
    {
        var owner = Owner();
        var current = owner.Read().Notifications;
        var next = property switch
        {
            nameof(FrontendNotifications.MemoryProtectionNotify) => current with { MemoryProtectionNotify = value },
            nameof(FrontendNotifications.MemoryProtectionSystem) => current with { MemoryProtectionSystem = value },
            nameof(FrontendNotifications.MemoryProtectionInApp) => current with { MemoryProtectionInApp = value },
            nameof(FrontendNotifications.UpdateNotify) => current with { UpdateNotify = value },
            nameof(FrontendNotifications.UpdateSystem) => current with { UpdateSystem = value },
            nameof(FrontendNotifications.UpdateInApp) => current with { UpdateInApp = value },
            nameof(FrontendNotifications.AccessRequestNotify) => current with { AccessRequestNotify = value },
            nameof(FrontendNotifications.AccessRequestSystem) => current with { AccessRequestSystem = value },
            nameof(FrontendNotifications.AccessRequestInApp) => current with { AccessRequestInApp = value },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, null)
        };
        owner.SaveGlobal(owner.Read() with { Notifications = next });
    }

    private static object FactsFor(string kind) => kind switch
    {
        Memory => TreeFacts(),
        Update => UpdateFacts("1.0.0"),
        _ => AccessFacts()
    };

    private static object ProcessFacts() =>
        new { actionId = "action-1", targetKind = "process", name = "game.exe", processId = 4242, recordCount = 1, bytesWritten = 1536 };

    private static object TreeFacts() =>
        new { actionId = "action-2", targetKind = "processTree", name = "launcher.exe", processId = 100, recordCount = 7, bytesWritten = 1048576 };

    private static object UpdateFacts(string version) => new { version };

    private static object AccessFacts() =>
        new { requestId = "request-1", name = "Example agent", observedExecutablePath = @"C:\Tools\agent.exe" };

    private static object FrameObject(params (long Sequence, string Kind, object Facts)[] items) => new
    {
        notifications = items.Select(item => new
        {
            sequence = item.Sequence, kind = item.Kind, occurredAt = "2026-10-29T08:00:00Z", facts = item.Facts
        }).ToArray()
    };

    private static JsonElement Frame(params (long Sequence, string Kind, object Facts)[] items) =>
        JsonSerializer.SerializeToElement(FrameObject(items), WireJson);

    private static HttpResponseMessage Response(params object[] frames) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join('\n', frames.Select(frame => JsonSerializer.Serialize(frame, WireJson))))
    };

    private sealed class CaptureTrace : TraceListener
    {
        internal readonly List<string> Lines = [];
        public override void Write(string? message) { if (message is not null) lock (Lines) Lines.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
}

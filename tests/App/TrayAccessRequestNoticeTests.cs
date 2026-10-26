using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

[CollectionDefinition("Tray access notices", DisableParallelization = true)]
public sealed class TrayAccessNoticeCollection { }

[Collection("Tray access notices")]
public sealed class TrayAccessRequestNoticeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-tray-access-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Title, string Message)> balloons = [];
    private bool visible;
    private string Cache => Path.Combine(root, "frontend-preferences.json");
    private FrontendPreferencesOwner Owner() => new(Cache);
    private TrayAccessRequestNotice Notice(string language = "en-US", Action<Action>? dispatch = null) =>
        new(Owner(), () => visible, (title, message) => balloons.Add((title, message)),
            dispatch ?? (action => action()), () => NativeTextCatalog.For(language));
    public TrayAccessRequestNoticeTests() => Directory.CreateDirectory(root);

    [Fact]
    public void HiddenNewRequestShowsOnceAcrossRestartAndSecondRequestShowsOnce()
    {
        var notice = Notice();
        var first = Request("first");
        var second = Request("second");
        notice.ObservePending([first]);
        Assert.Single(balloons);
        Assert.Equal(new[] { "first" }, Owner().ReadSystemAccessRequestNotifiedIds());
        notice.ObservePending([first]);
        Notice().ObservePending([first]);
        Assert.Single(balloons);
        Notice().ObservePending([first, second]);
        Assert.Equal(2, balloons.Count);
        Assert.Equal(new[] { "first", "second" }, Owner().ReadSystemAccessRequestNotifiedIds());
    }

    [Fact]
    public void VisibleRecordsNothingAndNextFrameAfterHidingNotifies()
    {
        var notice = Notice();
        visible = true;
        notice.ObservePending([Request("first")]);
        Assert.Empty(balloons);
        Assert.Empty(Owner().ReadSystemAccessRequestNotifiedIds());
        visible = false;
        notice.ObservePending([Request("first")]);
        Assert.Single(balloons);
        Assert.Equal(new[] { "first" }, Owner().ReadSystemAccessRequestNotifiedIds());
    }

    [Fact]
    public void SwitchIsReadAtDecisionAndOffDoesNotRecord()
    {
        var notice = Notice();
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with { Notifications = new() { AccessRequestSystem = "off" } });
        notice.ObservePending([Request("first")]);
        Assert.Empty(balloons);
        Assert.Empty(owner.ReadSystemAccessRequestNotifiedIds());
        owner.SaveGlobal(owner.Read() with { Notifications = new() { AccessRequestSystem = "on" } });
        notice.ObservePending([Request("first")]);
        Assert.Single(balloons);
    }

    [Theory]
    [InlineData(true, "on")]
    [InlineData(false, "off")]
    public void DepartedIdsArePrunedEvenWhenNotificationsCannotShow(bool windowVisible, string setting)
    {
        var notice = Notice();
        notice.ObservePending([Request("first"), Request("second")]);
        visible = windowVisible;
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with { Notifications = new() { AccessRequestSystem = setting } });
        notice.ObservePending([Request("second")]);
        Assert.Equal(new[] { "second" }, Owner().ReadSystemAccessRequestNotifiedIds());
        notice.ObservePending([]);
        Assert.Empty(Owner().ReadSystemAccessRequestNotifiedIds());
        Assert.Equal(2, balloons.Count);
    }

    [Fact]
    public void BoundNeverEvictsPendingIdsOrShowsUnrecordableNoticesAndPruningFreesSpace()
    {
        var limit = FrontendPreferencesOwner.MaximumAccessRequestNotificationIds;
        Assert.Equal(128, limit);
        var pending = Enumerable.Range(0, limit + 1).Select(index => Request("request-" + index)).ToArray();
        Notice().ObservePending(pending);
        Assert.Equal(limit, balloons.Count);
        Assert.Equal(limit, Owner().ReadSystemAccessRequestNotifiedIds().Length);
        Notice().ObservePending(pending.Reverse().ToArray());
        Assert.Equal(limit, balloons.Count);
        Assert.Throws<InvalidDataException>(() => Owner().RecordSystemAccessRequestNotifiedId("over-bound"));
        Notice().ObservePending([pending[^1]]);
        Assert.Equal(limit + 1, balloons.Count);
        Assert.Equal(new[] { pending[^1].RequestId }, Owner().ReadSystemAccessRequestNotifiedIds());
    }

    [Fact]
    public void LegacyVersionStateFrontendSavesResetAndVersionWritesAllPreserveBothRecords()
    {
        File.WriteAllText(Cache + ".native-notifications.json", """{"updateSystemNotifiedVersion":"1.2.0"}""");
        Notice().ObservePending([Request("first")]);
        var owner = Owner();
        Assert.Equal("1.2.0", owner.ReadSystemUpdateNotifiedVersion());
        owner.SaveGlobal(owner.Read() with { Notifications = new() { AccessRequestSystem = "off" } });
        Assert.Equal(new[] { "first" }, Owner().ReadSystemAccessRequestNotifiedIds());
        owner.Reset();
        owner.RecordSystemUpdateNotifiedVersion("1.3.0");
        Notice().ObservePending([Request("first")]);
        Assert.Single(balloons);
        Assert.Equal(new[] { "first" }, Owner().ReadSystemAccessRequestNotifiedIds());
        Assert.Equal("1.3.0", Owner().ReadSystemUpdateNotifiedVersion());
        // Access notices can also create the shared file before any version notice exists.
        File.Delete(Cache + ".native-notifications.json");
        Notice().ObservePending([Request("second")]);
        Assert.Null(Owner().ReadSystemUpdateNotifiedVersion());
    }

    [Fact]
    public void EveryShellLanguageShowsSelfDeclaredNameAndObservedFileNameAsPlainData()
    {
        const string name = "Self <b>{1}</b>";
        const string filename = "request{0}.exe";
        foreach (var language in NativeLanguage.SupportedIds)
        {
            Notice(language).ObservePending([Request(language, name, @"C:\Untrusted\" + filename)]);
            var shown = balloons[^1];
            var text = NativeTextCatalog.For(language);
            Assert.Equal(text.AccessRequestTitle, shown.Title);
            Assert.Equal(string.Format(text.AccessRequestMessageFormat, name, filename), shown.Message);
            Assert.Contains(name, shown.Message);
            Assert.Contains(filename, shown.Message);
            Assert.DoesNotContain(@"C:\Untrusted\", shown.Message);
            if (language != "en-US")
            {
                Assert.NotEqual(NativeTextCatalog.For("en-US").AccessRequestTitle, shown.Title);
                Assert.NotEqual(NativeTextCatalog.For("en-US").AccessRequestMessageFormat, text.AccessRequestMessageFormat);
            }
        }
        Assert.Equal(NativeLanguage.SupportedIds.Count, balloons.Count);
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(403, 0)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(401, 2)]
    [InlineData(403, 2)]
    public async Task DeniedStreamBacksOffWithoutBalloonsPruningOrLogSpam(int status, int mode)
    {
        Notice().ObservePending([Request("already-notified")]);
        balloons.Clear();
        using var cancel = new CancellationTokenSource();
        using var log = new CaptureTrace();
        Trace.Listeners.Add(log);
        var delays = new List<int>();
        var attempts = 0;
        try
        {
            await Notice().RunAsync(_ =>
            {
                attempts++;
                if (mode == 2) return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Denied by session.", null, (HttpStatusCode)status));
                return Task.FromResult(mode == 1
                    ? Response(new { subscriptionId = "access-requests", value = new { statusCode = status, error = "access-scope-required" } })
                    : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("not a pending list") });
            }, cancel.Token, (duration, _) =>
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
        Assert.Equal(new[] { "already-notified" }, Owner().ReadSystemAccessRequestNotifiedIds());
        Assert.DoesNotContain(log.Lines, line => line.Contains("Tray notification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OneSharedStreamRoutesBothConsumersAndReconnectDoesNotRepeatEitherNotice()
    {
        var preferences = Owner();
        var access = Notice();
        var update = new TrayUpdateNotice(preferences, () => visible,
            (title, message) => balloons.Add((title, message)), action => action());
        using var cancel = new CancellationTokenSource();
        var attempts = 0;
        await TrayNotificationStream.RunAsync(_ =>
        {
            attempts++;
            return Task.FromResult(Response(
                new { subscriptionId = "updates", value = new { availableVersion = "1.2.0" } },
                new { subscriptionId = "access-requests", value = new[] { Request("first") } }));
        }, new Dictionary<string, Action<JsonElement>>
        {
            ["updates"] = update.ObserveFrame, ["access-requests"] = access.ObserveFrame
        }, action => action(), cancel.Token, (_, _) =>
        {
            if (attempts == 2) cancel.Cancel();
            return Task.CompletedTask;
        });
        Assert.Equal(2, attempts);
        Assert.Equal(2, balloons.Count);
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
        Assert.Equal(new[] { "first" }, Owner().ReadSystemAccessRequestNotifiedIds());
    }

    [Fact]
    public async Task RejectedAccessSelectorCannotBlockAuthorizedUpdateSelector()
    {
        var update = new TrayUpdateNotice(Owner(), () => visible,
            (title, message) => balloons.Add((title, message)), action => action());
        var access = Notice();
        using var cancel = new CancellationTokenSource();
        await TrayNotificationStream.RunAsync(_ => Task.FromResult(Response(
            new { subscriptionId = "access-requests", value = new { statusCode = 403, error = "access-scope-required" } },
            new { subscriptionId = "updates", value = new { availableVersion = "1.2.0" } })),
            new Dictionary<string, Action<JsonElement>>
            {
                ["updates"] = update.ObserveFrame, ["access-requests"] = access.ObserveFrame
            }, action => action(), cancel.Token, (_, _) => { cancel.Cancel(); return Task.CompletedTask; });
        Assert.Single(balloons);
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
        Assert.Empty(Owner().ReadSystemAccessRequestNotifiedIds());
    }

    [Fact]
    public async Task QueuedFrameReadsVisibilityAtDispatchAndCancellationPreventsLateNotice()
    {
        using var cancel = new CancellationTokenSource();
        var pending = new Queue<Action>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Notice(dispatch: pending.Enqueue).RunAsync(_ => Task.FromResult(Response(
            new { subscriptionId = "access-requests", value = new[] { Request("first") } },
            new { subscriptionId = "access-requests", value = new[] { Request("first") } })), cancel.Token,
            async (_, token) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        visible = true;
        pending.Dequeue()();
        Assert.Empty(Owner().ReadSystemAccessRequestNotifiedIds());
        visible = false;
        cancel.Cancel();
        pending.Dequeue()();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(balloons);
        Assert.Empty(Owner().ReadSystemAccessRequestNotifiedIds());
    }

    private static TrustedAccessRequestPendingWireSnapshot Request(string id, string name = "Requester", string path = @"C:\Tools\request.exe")
        => new(id, name, ["read"], path, 123, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    private static HttpResponseMessage Response(params object[] frames) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join('\n', frames.Select(frame => JsonSerializer.Serialize(frame, new JsonSerializerOptions(JsonSerializerDefaults.Web)))))
    };

    private sealed class CaptureTrace : TraceListener
    {
        internal readonly List<string> Lines = [];
        public override void Write(string? message) { if (message is not null) Lines.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
}

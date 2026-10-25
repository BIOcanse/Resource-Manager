using System.Net;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

public sealed class TrayUpdateNoticeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-tray-update-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Title, string Message)> balloons = [];
    private bool visible;
    private string Cache => Path.Combine(root, "frontend-preferences.json");
    private FrontendPreferencesOwner Owner() => new(Cache);
    private TrayUpdateNotice Notice(Action<Action>? dispatch = null, string language = "en-US") =>
        new(Owner(), () => visible, (title, message) => balloons.Add((title, message)),
            dispatch ?? (action => action()), () => NativeTextCatalog.For(language));

    public TrayUpdateNoticeTests() => Directory.CreateDirectory(root);

    [Fact]
    public void HiddenNewVersionShowsOnceAndRecordsThenNewerVersionShowsOnce()
    {
        var notice = Notice();
        notice.ObserveVersion("1.2.0");
        Assert.Single(balloons);
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
        notice.ObserveVersion("1.2.0");
        Notice().ObserveVersion("1.2.0"); // Restart both the tray consumer and its state owner.
        Assert.Single(balloons);
        Notice().ObserveVersion("1.3.0");
        Assert.Equal(2, balloons.Count);
        Assert.Equal("1.3.0", Owner().ReadSystemUpdateNotifiedVersion());
    }

    [Fact]
    public void VisibleDoesNotRecordAndRepeatedFrameAfterClosingCanNotify()
    {
        var notice = Notice();
        visible = true;
        notice.ObserveVersion("1.2.0");
        Assert.Empty(balloons);
        Assert.Null(Owner().ReadSystemUpdateNotifiedVersion());
        visible = false;
        notice.ObserveVersion("1.2.0");
        Assert.Single(balloons);
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
    }

    [Fact]
    public void SwitchIsReadAtDecisionAndOffDoesNotRecord()
    {
        var notice = Notice();
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with { Notifications = new() { UpdateSystem = "off" } });
        notice.ObserveVersion("1.2.0");
        Assert.Empty(balloons);
        Assert.Null(owner.ReadSystemUpdateNotifiedVersion());
        owner.SaveGlobal(owner.Read() with { Notifications = new() { UpdateSystem = "on" } });
        notice.ObserveVersion("1.2.0");
        Assert.Single(balloons);
    }

    [Fact]
    public void NullVersionDoesNothing()
    {
        Notice().ObserveVersion(null);
        Assert.Empty(balloons);
        Assert.Null(Owner().ReadSystemUpdateNotifiedVersion());
    }

    [Fact]
    public void FrontendWholeNotificationSaveAndResetCannotEraseNativeRecord()
    {
        Notice().ObserveVersion("1.2.0");
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with { Notifications = new() { UpdateSystem = "off" } });
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
        owner.Reset();
        Notice().ObserveVersion("1.2.0");
        Assert.Single(balloons);
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
    }

    [Fact]
    public async Task EndedStreamReconnectsWithBoundedBackoffWithoutRepeatingFrames()
    {
        using var cancel = new CancellationTokenSource();
        var attempts = 0;
        var delays = new List<TimeSpan>();
        await Notice().RunAsync(_ =>
        {
            attempts++;
            return Task.FromResult(Frames("1.2.0", "1.2.0"));
        }, cancel.Token, (duration, _) =>
        {
            delays.Add(duration);
            if (delays.Count == 7) cancel.Cancel();
            return Task.CompletedTask;
        });
        Assert.Equal(7, attempts);
        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30, 30 }, delays.Select(value => (int)value.TotalSeconds));
        Assert.Single(balloons);
        Assert.Equal("1.2.0", Owner().ReadSystemUpdateNotifiedVersion());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task DeniedStreamShowsNothingAndWaitsBeforeRetry(HttpStatusCode status)
    {
        using var cancel = new CancellationTokenSource();
        var waiting = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var run = Notice().RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            // Even a denial body resembling a valid frame must never be consumed.
            var response = Frames("1.2.0");
            response.StatusCode = status;
            return Task.FromResult(response);
        }, cancel.Token, async (duration, token) =>
        {
            waiting.SetResult(duration);
            await release.Task.WaitAsync(token);
        });
        Assert.Equal(TimeSpan.FromSeconds(2), await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Empty(balloons);
        Assert.Null(Owner().ReadSystemUpdateNotifiedVersion());
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueuedFrameUsesCurrentVisibilityAndCancelledGenerationCannotNotify()
    {
        using var cancel = new CancellationTokenSource();
        var pending = new Queue<Action>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Notice(pending.Enqueue).RunAsync(_ => Task.FromResult(Frames("1.2.0", "1.2.0")), cancel.Token,
            async (_, token) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        visible = true;
        pending.Dequeue()();
        Assert.Empty(balloons);
        Assert.Null(Owner().ReadSystemUpdateNotifiedVersion());
        visible = false;
        cancel.Cancel();
        pending.Dequeue()();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(balloons);
    }

    [Fact]
    public async Task JsonFramesIgnoreOtherSubscriptionsAndUseOnlyAvailableVersionForCopy()
    {
        using var cancel = new CancellationTokenSource();
        const string frames = """
            {"subscriptionId":"other","value":{"availableVersion":"9.0"}}
            {"subscriptionId":"updates","value":{"availableVersion":null}}
            {"subscriptionId":"updates","value":{"availableVersion":"1.2.0","message":"UNTRUSTED COPY","title":"UNTRUSTED TITLE"}}
            """;
        await Notice().RunAsync(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(frames) }), cancel.Token,
            (_, _) => { cancel.Cancel(); return Task.CompletedTask; });
        var shown = Assert.Single(balloons);
        Assert.Equal(NativeTextCatalog.For("en-US").UpdateAvailableTitle, shown.Title);
        Assert.Equal(string.Format(NativeTextCatalog.For("en-US").UpdateAvailableMessageFormat, "1.2.0"), shown.Message);
        Assert.DoesNotContain("UNTRUSTED", shown.Message);
    }

    [Fact]
    public void EverySelectableLanguageFormatsTheActualBalloonWithTranslatedCopy()
    {
        var english = NativeTextCatalog.For("en-US");
        foreach (var language in NativeLanguage.SupportedIds)
        {
            Notice(language: language).ObserveVersion("1.2.0-" + language);
            var shown = balloons[^1];
            Assert.Equal(NativeTextCatalog.For(language).UpdateAvailableTitle, shown.Title);
            Assert.Contains("1.2.0-" + language, shown.Message);
            Assert.DoesNotContain("{0}", shown.Message);
            if (language != "en-US")
            {
                Assert.NotEqual(english.UpdateAvailableTitle, shown.Title);
                Assert.NotEqual(english.UpdateAvailableMessageFormat, NativeTextCatalog.For(language).UpdateAvailableMessageFormat);
            }
        }
        Assert.Equal(NativeLanguage.SupportedIds.Count, balloons.Count);
    }

    private static HttpResponseMessage Frames(params string?[] versions) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join('\n', versions.Select(version => JsonSerializer.Serialize(new
        { subscriptionId = "updates", value = new { availableVersion = version } }))))
    };

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
}

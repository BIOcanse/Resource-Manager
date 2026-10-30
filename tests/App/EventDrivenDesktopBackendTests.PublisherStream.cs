using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.Shared.Runtime;

namespace Resource_Manager_APP.Tests;

// The desktop publisher stream through the production pipeline: the real endpoint, trusted access
// and store, with this test process as the publisher.
public sealed partial class EventDrivenDesktopBackendTests
{
    [Theory]
    [InlineData("body-end")]
    [InlineData("abort")]
    [InlineData("revoke")]
    public async Task ProductionPublisherStreamLinesReplaceTheObservationUntilTheStreamEnds(string end)
    {
        await using var fixture = await Fixture.StartAsync();
        var desktop = Desktop.Current();
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var store = fixture.App.Services.GetRequiredService<InteractiveDesktopObservationStore>();
        await using var stream = new PublisherStream(client, timeout.Token);

        stream.Write(desktop.Focused(0));
        await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) == true, timeout.Token);
        // The next line replaces it: another process is now in the foreground.
        stream.Write(desktop.Behind(1));
        await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) == false, timeout.Token);

        switch (end)
        {
            case "body-end":
                Assert.Equal(HttpStatusCode.OK, await stream.EndAsync());
                break;
            case "abort":
                stream.Abort();
                break;
            default:
                Assert.True(await fixture.Credential.Owner.RevokeAsync(fixture.Credential.CredentialId, timeout.Token));
                break;
        }
        await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) is null, timeout.Token);
        Assert.Empty(store.Read([new(desktop.ProcessId, desktop.Start)]).Processes);
    }

    [Fact]
    public async Task ProductionSecondPublisherStreamInTheSameSessionIsRefused()
    {
        await using var fixture = await Fixture.StartAsync();
        var desktop = Desktop.Current();
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var store = fixture.App.Services.GetRequiredService<InteractiveDesktopObservationStore>();
        await using var first = new PublisherStream(client, timeout.Token);
        first.Write(desktop.Focused(0));
        await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) == true, timeout.Token);

        await using (var second = new PublisherStream(client, timeout.Token))
            Assert.Equal(HttpStatusCode.Conflict, await second.EndAsync());

        // The refusal leaves the first stream selected and its observation current.
        Assert.True(store.IsForeground(desktop.ProcessId, desktop.Start));
        Assert.Equal(HttpStatusCode.OK, await first.EndAsync());
        Assert.Null(store.IsForeground(desktop.ProcessId, desktop.Start));
    }

    [Fact]
    public async Task ProductionInvalidPublisherStreamLineEndsTheStreamAndReleasesTheSelection()
    {
        await using var fixture = await Fixture.StartAsync();
        var desktop = Desktop.Current();
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var store = fixture.App.Services.GetRequiredService<InteractiveDesktopObservationStore>();
        await using (var stream = new PublisherStream(client, timeout.Token))
        {
            stream.Write(desktop.Focused(0));
            await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) == true, timeout.Token);
            stream.WriteLine("{not json}");
            await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) is null, timeout.Token);
        }

        // The selection went with the stream, so a new stream is selected.
        await using var next = new PublisherStream(client, timeout.Token);
        next.Write(desktop.Focused(1));
        await WaitAsync(() => store.IsForeground(desktop.ProcessId, desktop.Start) == true, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, await next.EndAsync());
        Assert.Null(store.IsForeground(desktop.ProcessId, desktop.Start));
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    // This test process as a publisher of its own Windows session.
    private sealed record Desktop(int ProcessId, long Start, int SessionId, long ObservedAt)
    {
        internal static Desktop Current()
        {
            using var process = Process.GetCurrentProcess();
            Assert.True(process.SessionId > 0);
            return new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), process.SessionId,
                DateTimeOffset.UtcNow.AddSeconds(-1).UtcTicks);
        }

        // This process is in the foreground.
        internal InteractiveDesktopObservation Focused(int step)
        {
            var window = Window();
            return new(ProcessId, Start, SessionId, ObservedAt + step, true, true, window, [window]);
        }

        // Another process is in the foreground; this one's window stays visible behind it.
        internal InteractiveDesktopObservation Behind(int step)
        {
            var other = new DesktopProcessWindowObservation(ProcessId + 4,
                DesktopProcessWindowObservation.UnverifiedCreationTime, true, false);
            return new(ProcessId, Start, SessionId, ObservedAt + step, true, true, other, [Window(), other]);
        }

        private DesktopProcessWindowObservation Window() => new(ProcessId, Start, true, false);
    }

    // One publisher stream request whose chunked application/x-ndjson body is written line by line
    // by the test.
    private sealed class PublisherStream : IAsyncDisposable
    {
        private readonly Channel<byte[]> lines = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource abort;
        private readonly Task<HttpResponseMessage> response;

        internal PublisherStream(HttpClient client, CancellationToken token)
        {
            abort = CancellationTokenSource.CreateLinkedTokenSource(token);
            var request = new HttpRequestMessage(HttpMethod.Post, InteractiveDesktopObservation.PublisherStreamPath)
            { Content = new Body(lines.Reader) };
            response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        }

        internal void Write(InteractiveDesktopObservation sample)
            => WriteLine(JsonSerializer.Serialize(sample, JsonSerializerOptions.Web));

        internal void WriteLine(string line) => Assert.True(lines.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n")));

        // Ends the body with its terminating chunk and reads the response to its end, which the
        // backend reaches only after the stream has released its selection.
        internal async Task<HttpStatusCode> EndAsync()
        {
            lines.Writer.TryComplete();
            using var ended = await response;
            await ended.Content.ReadAsStringAsync(abort.Token);
            return ended.StatusCode;
        }

        internal void Abort() => abort.Cancel();

        public async ValueTask DisposeAsync()
        {
            lines.Writer.TryComplete();
            // Already ended, aborted or refused by the test.
            try { (await response.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); }
            catch (Exception) { }
            finally
            {
                abort.Cancel();
                abort.Dispose();
            }
        }

        private sealed class Body : HttpContent
        {
            private readonly ChannelReader<byte[]> lines;

            internal Body(ChannelReader<byte[]> lines)
            {
                this.lines = lines;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
                CancellationToken cancellationToken)
            {
                await foreach (var line in lines.ReadAllAsync(cancellationToken))
                {
                    await stream.WriteAsync(line, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }
}

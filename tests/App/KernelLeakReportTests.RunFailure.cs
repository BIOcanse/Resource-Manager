using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SourceFailureOrEndOnlyRecordsARunWhenAFrameStartedIt(bool deliverFrame, bool endNormally)
    {
        var source = new EndingSource(deliverFrame, endNormally);
        var before = DateTimeOffset.UtcNow;
        using var fixture = new Fixture(sampleSource: source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (fixture.Reports.Read().Status != KernelLeakReportStatus.Unavailable)
            await Task.Delay(5, timeout.Token);
        var failed = fixture.Reports.Read();
        Assert.Equal(KernelLeakFailure.SourceUnavailable, failed.Failure);
        Assert.Equal(deliverFrame ? DateTimeOffset.UnixEpoch : (DateTimeOffset?)null, failed.Observation.StartedAt);
        Assert.Equal(deliverFrame ? 1 : 0, failed.Observation.SampleCount);
        Assert.Null(failed.Observation.PreviousRunFailure);
        Assert.InRange(failed.CapturedAt!.Value, before, DateTimeOffset.UtcNow);
        if (!deliverFrame) Assert.Equal(0, failed.Observation.ElapsedSeconds);

        var next = DateTimeOffset.UnixEpoch.AddSeconds(2);
        source.Recovery.Writer.TryWrite(new(next, true, []));
        while (fixture.Reports.Read().CapturedAt != next) await Task.Delay(5, timeout.Token);
        var recovered = fixture.Reports.Read();
        Assert.Equal(KernelLeakFailure.None, recovered.Failure);
        Assert.Equal(next, recovered.Observation.StartedAt);
        if (deliverFrame)
        {
            var previous = Assert.IsType<KernelLeakRunFailure>(recovered.Observation.PreviousRunFailure);
            Assert.Equal(DateTimeOffset.UnixEpoch, previous.StartedAt);
            Assert.Equal(failed.CapturedAt.Value, previous.FailedAt);
            using var wire = JsonDocument.Parse(SerializeProduction(recovered));
            Assert.Equal(DateTimeOffset.UnixEpoch,
                wire.RootElement.GetProperty("observation").GetProperty("previousRunFailure").GetProperty("startedAt").GetDateTimeOffset());
        }
        else Assert.Null(recovered.Observation.PreviousRunFailure);
    }

    [Fact]
    public async Task InvalidFrameFailureKeepsTheRealRunStart()
    {
        using var fixture = new Fixture(rawSamples: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var began = DateTimeOffset.UnixEpoch.AddSeconds(5);
        fixture.Raw!.Push(new(began, true, []));
        Assert.True(await reader.MoveNextAsync());
        var invalidAt = began.AddSeconds(-1);
        fixture.Raw.Push(new(invalidAt, true, []));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakFailure.InvalidObservation, reader.Current.Failure);
        Assert.Equal(began, reader.Current.Observation.StartedAt);
        fixture.Raw.Push(new(began.AddSeconds(1), true, []));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(new KernelLeakRunFailure(began, invalidAt, KernelLeakFailure.InvalidObservation),
            reader.Current.Observation.PreviousRunFailure);
    }

    private sealed class EndingSource(bool deliverFrame, bool endNormally) : IKernelLeakSampleSource
    {
        private int subscriptions;
        internal Channel<KernelLeakSampleFrame> Recovery { get; } = Channel.CreateBounded<KernelLeakSampleFrame>(1);
        public async IAsyncEnumerable<KernelLeakSampleFrame> SubscribeAsync(string holder, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref subscriptions);
            if (attempt == 1)
            {
                if (deliverFrame) yield return new(DateTimeOffset.UnixEpoch, true, []);
                if (!endNormally) throw new IOException("Recorded sampling subscription failure.");
                yield break;
            }
            // Reacquiring the source without a frame must not manufacture another run
            // or overwrite the original run failure's timestamps.
            if (attempt == 2) throw new IOException("Recorded retry failure before a frame.");
            await foreach (var frame in Recovery.Reader.ReadAllAsync(cancellationToken)) yield return frame;
        }
    }
}

using System.Text.Json;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    [Fact]
    public async Task InitialSourceWarmupDoesNotPublishAnUnavailableFailure()
    {
        using var fixture = new Fixture();
        var at = DateTimeOffset.UnixEpoch;
        fixture.Processes.Push(new(at, []) { Sampling = new(ResourceBreakdownSamplingStatuses.Warming, 0, at, null, null, null) });
        await DeliverWithoutReader(fixture, 1, 1000);
        var report = fixture.Reports.Read();
        Assert.Equal(KernelLeakReportStatus.Observing, report.Status);
        Assert.Equal(KernelLeakFailure.None, report.Failure);
        Assert.Equal(1, report.Observation.SampleCount);
        Assert.Equal(1, fixture.Reports.ReadDiagnostics().ObservedFrames);
        Assert.Null(report.Observation.PreviousRunFailure);
    }
    [Fact]
    public async Task HostLifetimeWaitsForCompiledPlanPublicationBeforeAcquiringOwnSubscription()
    {
        using var fixture = new Fixture(publishAfterHostStart: true);
        await fixture.Processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, fixture.Processes.Starts);
        Assert.Equal(KernelLeakReportStatus.Observing, fixture.Reports.Read().Status);
        await fixture.StopAsync();
        Assert.Equal(0, fixture.Processes.Active);
    }
    [Fact]
    public async Task OwnSubscriptionObservesWithZeroReportReadersAndSurvivesReaderDeparture()
    {
        using var fixture = new Fixture();
        await fixture.Processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, fixture.Processes.Starts);
        Assert.Equal("kernel-leak-observer", fixture.Processes.Holder);
        for (var index = 0; index < 3; index++) await DeliverWithoutReader(fixture, index, 1000 + index * 100);
        Assert.Equal(KernelLeakReportStatus.Suspected, fixture.Reports.Read().Status);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reader = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakReportStatus.Suspected, reader.Current.Status);
        await reader.DisposeAsync();
        await DeliverWithoutReader(fixture, 3, 1300);
        Assert.Equal(4, fixture.Reports.Read().Observation.SampleCount);
        Assert.Equal(1, fixture.Processes.Active);
        await fixture.StopAsync();
        Assert.Equal(0, fixture.Processes.Active);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().SeriesBytes);
    }
    [Fact]
    public async Task DisabledObservationIsTypedAndReadersNeverAcquireSamplingSubscription()
    {
        using var fixture = new Fixture(options: Fast with { Enabled = false });
        Assert.Equal(KernelLeakReportStatus.NotObserved, fixture.Reports.Read().Status);
        Assert.Equal(KernelLeakFailure.None, fixture.Reports.Read().Failure);
        Assert.Null(fixture.Reports.Read().Observation.StartedAt);
        Assert.Equal(0, fixture.Reports.Read().Observation.SampleCount);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakReportStatus.NotObserved, reader.Current.Status);
        Assert.Equal(0, fixture.Processes.Starts);
        Assert.Equal(0, fixture.Processes.Active);
        WriteEvidence("disabled-wire-example.json", SerializeProduction(reader.Current));
    }
    [Fact]
    public async Task ProgressUsesCompiledRequirementsAndActualSampleSpan()
    {
        using var fixture = new Fixture();
        var initial = fixture.Reports.Read();
        Assert.Equal(KernelLeakReportStatus.Observing, initial.Status);
        Assert.Null(initial.Observation.StartedAt);
        Assert.Equal(0, initial.Observation.ElapsedSeconds);
        Assert.Equal(0, initial.Observation.SampleCount);
        Assert.Equal(2, initial.Observation.RequiredSeconds);
        Assert.Equal(3, initial.Observation.RequiredSampleCount);
        await DeliverWithoutReader(fixture, 0, 800);
        await DeliverWithoutReader(fixture, 1, 900);
        var observing = fixture.Reports.Read();
        Assert.Equal(KernelLeakReportStatus.Observing, observing.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch, observing.Observation.StartedAt);
        Assert.Equal(1, observing.Observation.ElapsedSeconds);
        Assert.Equal(2, observing.Observation.SampleCount);
        WriteEvidence("observing-wire-example.json", SerializeProduction(observing));
        await DeliverWithoutReader(fixture, 2, 1000);
        Assert.Equal(KernelLeakReportStatus.Empty, fixture.Reports.Read().Status);
        Assert.Equal(2, fixture.Reports.Read().Observation.ElapsedSeconds);
    }
    [Fact]
    public async Task CapacityFailureIsVisibleUntilNextFrameThenPreviousFailureSurvivesNewRun()
    {
        using var fixture = new Fixture(rawSamples: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch, true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1000)]));
        Assert.True(await reader.MoveNextAsync());
        var failedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        fixture.Raw.Push(new(failedAt, true, Enumerable.Range(1, 9)
            .Select(pid => new KernelLeakSample(pid, "42", KernelLeakSampleKind.ProcessHandles, 1200)).ToArray()));
        Assert.True(await reader.MoveNextAsync());
        var failure = reader.Current;
        Assert.Equal(KernelLeakReportStatus.Unavailable, failure.Status);
        Assert.Equal(KernelLeakFailure.CapacityExceeded, failure.Failure);
        Assert.Equal(DateTimeOffset.UnixEpoch, failure.Observation.StartedAt);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().HolderCount);
        Assert.Same(failure, fixture.Reports.Read()); // Reader churn cannot reset a failed run.
        WriteEvidence("failed-wire-example.json", SerializeProduction(failure));
        var nextAt = failedAt.AddSeconds(1);
        fixture.Raw.Push(new(nextAt, true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1400)]));
        Assert.True(await reader.MoveNextAsync());
        var restarted = reader.Current;
        Assert.Equal(KernelLeakReportStatus.Observing, restarted.Status);
        Assert.Equal(KernelLeakFailure.None, restarted.Failure);
        Assert.Equal(nextAt, restarted.Observation.StartedAt);
        Assert.Equal(1, restarted.Observation.SampleCount);
        Assert.Equal(0, restarted.Observation.ElapsedSeconds);
        Assert.Equal(new KernelLeakRunFailure(DateTimeOffset.UnixEpoch, failedAt, KernelLeakFailure.CapacityExceeded), restarted.Observation.PreviousRunFailure);
        Assert.Equal(KernelLeakRestartPolicy.NextInterval, restarted.Observation.RestartPolicy);
        WriteEvidence("restarted-wire-example.json", SerializeProduction(restarted));
        fixture.Raw.Push(new(nextAt.AddSeconds(1), true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1500)]));
        Assert.True(await reader.MoveNextAsync());
        Assert.Empty(reader.Current.Items); // Old evidence was released.
        Assert.Equal(restarted.Observation.PreviousRunFailure, reader.Current.Observation.PreviousRunFailure);
    }
    [Fact]
    public async Task NextIntervalStartsVisibleRunEvenWhenTheNextAttemptIsUnavailable()
    {
        using var fixture = new Fixture(rawSamples: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch, true, Enumerable.Range(1, 9)
            .Select(pid => new KernelLeakSample(pid, "42", KernelLeakSampleKind.ProcessHandles, 1200)).ToArray()));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakFailure.CapacityExceeded, reader.Current.Failure);
        var next = DateTimeOffset.UnixEpoch.AddSeconds(1);
        fixture.Raw.Push(new(next, false, []));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(next, reader.Current.Observation.StartedAt);
        Assert.Equal(0, reader.Current.Observation.SampleCount);
        Assert.Equal(KernelLeakFailure.SourceUnavailable, reader.Current.Failure);
        Assert.Equal(KernelLeakFailure.CapacityExceeded, reader.Current.Observation.PreviousRunFailure!.Failure);
        fixture.Raw.Push(new(next.AddSeconds(1), true, [new(7, "42", KernelLeakSampleKind.ProcessHandles, 1300)]));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(KernelLeakReportStatus.Observing, reader.Current.Status);
        Assert.Equal(next.AddSeconds(1), reader.Current.Observation.StartedAt);
        Assert.Equal(KernelLeakFailure.SourceUnavailable, reader.Current.Observation.PreviousRunFailure!.Failure);
    }
    [Theory]
    [InlineData("absent", KernelLeakMetadataStatus.Current, KernelLeakMetadataStatus.Current)]
    [InlineData("empty-product", KernelLeakMetadataStatus.Current, KernelLeakMetadataStatus.Current)]
    [InlineData("product-failure", KernelLeakMetadataStatus.Unavailable, KernelLeakMetadataStatus.Current)]
    [InlineData("product-overflow", KernelLeakMetadataStatus.Unavailable, KernelLeakMetadataStatus.Current)]
    [InlineData("ended", KernelLeakMetadataStatus.ProcessEnded, KernelLeakMetadataStatus.ProcessEnded)]
    [InlineData("reused", KernelLeakMetadataStatus.ProcessEnded, KernelLeakMetadataStatus.ProcessEnded)]
    [InlineData("identity-unavailable", KernelLeakMetadataStatus.Unavailable, KernelLeakMetadataStatus.Unavailable)]
    public async Task RecordedNativeMetadataStatesAreConsistent(string outcome, KernelLeakMetadataStatus imageStatus, KernelLeakMetadataStatus serviceStatus)
    {
        var native = new WindowsKernelLeakAttributionReader((_, _) => "C:\\recorded\\holder.exe",
            _ => outcome switch
            {
                "ended" => new(ProcessReadStatus.Exited, 42),
                "reused" => new(ProcessReadStatus.Current, 43),
                "identity-unavailable" => new(ProcessReadStatus.Unavailable, null),
                _ => new(ProcessReadStatus.Current, 42)
            }, _ => outcome switch { "product-failure" => throw new IOException("Recorded FileVersionInfo failure"),
                "product-overflow" => new string('x', 32769), "empty-product" => "", _ => null },
            _ => new() { [7] = ["recorded-service"] });
        using var fixture = new Fixture(attribution: native);
        for (var index = 0; index < 3; index++) await DeliverWithoutReader(fixture, index, 1000 + index * 100);
        var report = fixture.Reports.Read();
        var holder = Assert.Single(report.Items).Holder;
        Assert.Equal(imageStatus, holder.Image.Status);
        Assert.Equal(serviceStatus, holder.Services.Status);
        Assert.Null(holder.Image.ProductName);
        if (imageStatus != KernelLeakMetadataStatus.Current)
        {
            Assert.Null(holder.Image.Name); Assert.Null(holder.Image.Path);
            using var json = JsonDocument.Parse(SerializeProduction(report));
            var image = json.RootElement.GetProperty("items")[0].GetProperty("holder").GetProperty("image");
            Assert.Single(image.EnumerateObject());
        }
        if (serviceStatus != KernelLeakMetadataStatus.Current) Assert.Empty(holder.Services.Names);
    }
    [Fact]
    public async Task PublishedKindsUnitsAndReservedActionsAreConsumerReady()
    {
        Assert.Equal(["ProcessHandles"], Enum.GetNames<KernelLeakResourceKind>());
        Assert.Equal(["Count"], Enum.GetNames<KernelLeakUnit>());
        Assert.DoesNotContain("NotObserved", Enum.GetNames<KernelLeakFailure>());
        using var fixture = new Fixture();
        for (var index = 0; index < 3; index++) await DeliverWithoutReader(fixture, index, 1000 + index * 100);
        var report = fixture.Reports.Read();
        var item = Assert.Single(report.Items);
        Assert.Equal(KernelLeakUnit.Count, item.Unit);
        Assert.Equal([KernelLeakActionKind.RestartHolder, KernelLeakActionKind.ForceReleaseHandles], item.Actions.Select(action => action.Action));
        foreach (var action in item.Actions)
        {
            Assert.False(action.Enabled);
            Assert.Equal(BackendMessageDomains.KernelLeak, action.DisabledReason!.Domain);
            Assert.Equal(BackendMessageCodes.KernelLeak.ActionNotProvided, action.DisabledReason.Code);
            Assert.Empty(action.DisabledReason.Args);
            Assert.Equal(BackendMessageDomains.KernelLeak, action.Risk.Domain);
            Assert.Empty(action.Risk.Args);
        }
        Assert.Equal(BackendMessageCodes.KernelLeak.RestartHolderRisk, item.Actions[0].Risk.Code);
        Assert.Equal(KernelLeakRiskLevel.Warning, item.Actions[0].RiskLevel);
        Assert.Equal(BackendMessageCodes.KernelLeak.ForceReleaseHandlesRisk, item.Actions[1].Risk.Code);
        Assert.Equal(KernelLeakRiskLevel.High, item.Actions[1].RiskLevel);
        var wire = SerializeProduction(report);
        Assert.Contains("\"unit\":\"count\"", wire);
        Assert.DoesNotContain("FileCachePoolBytes", wire);
        Assert.DoesNotContain("imageStatus", wire);
        WriteEvidence("v2-suspected-wire-example.json", wire);
    }
    private static string SerializeProduction(KernelLeakReport report)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManager.App.Endpoints.ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        return JsonSerializer.Serialize(report, options);
    }
    private static async Task DeliverWithoutReader(Fixture fixture, int second, int value)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(second);
        fixture.Processes.Push(new(at, []) { ProcessFields = new(at, null, [],
            [new(7, "42", [new(KernelLeakProcessSampleSource.HandleField, new MetricInteger((ulong)value), null)])]) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (fixture.Reports.Read().CapturedAt != at) await Task.Delay(5, timeout.Token);
    }
}

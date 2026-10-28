using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessIdentityFieldTests
{
    private static readonly string[] Names = ["process_all_start_time_actual", "process_all_image_path_actual",
        "process_all_parent_pid_actual", "process_all_user_sid_actual"];

    [Fact]
    public void AppendedCodesAndNativeParentLayoutPreserveOriginalValuesAndPointerWidth()
    {
        Assert.Equal(14, (int)ProcessMetricFieldCode.CpuUsage);
        Assert.Equal(15, (int)ProcessMetricFieldCode.StartTime); Assert.Equal(18, (int)ProcessMetricFieldCode.UserSid);
        Assert.Equal(10, Basic.Names.Length);
        Assert.Equal(6 * IntPtr.Size, Marshal.SizeOf<WindowsProcessBasicMetricNativeApi.ProcessBasicInformation>());
        Assert.Equal(5 * IntPtr.Size, Marshal.OffsetOf<WindowsProcessBasicMetricNativeApi.ProcessBasicInformation>("InheritedFromUniqueProcessId").ToInt32());
        Assert.Equal(0u, WindowsProcessBasicMetricNativeApi.DecodeInheritedPid(UIntPtr.Zero));
        Assert.Equal(uint.MaxValue, WindowsProcessBasicMetricNativeApi.DecodeInheritedPid(new UIntPtr(uint.MaxValue)));
        if (IntPtr.Size == 8) Assert.Null(WindowsProcessBasicMetricNativeApi.DecodeInheritedPid(new UIntPtr((ulong)uint.MaxValue + 1)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CanonicalOnlyReadsSelectedIdentityCategoryOnOneVerifiedHandle(int selected)
    {
        var api = new Basic.Api(); using var fixture = Basic.Fixture(api);
        var request = Basic.Request(Names[selected]);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        var value = Basic.Value(sample, Names[selected]);
        Assert.Equal(1, api.Opens); Assert.Equal(2, api.TimeCalls); Assert.Equal(1, api.Closes);
        Assert.Equal(selected == 1 ? 1 : 0, api.ImageCalls); Assert.Equal(selected == 2 ? 1 : 0, api.ParentCalls);
        Assert.Equal(selected == 3 ? 1 : 0, api.SidCalls); Assert.Equal(0, api.MemoryCalls + api.IoCalls + api.HandleCalls);
        Assert.Empty(sample.Bars); Assert.False(WindowsResourceBreakdownSampler.RequiresProcessAttribution(request));
        Assert.Null(sample.ProcessFields!.WindowMilliseconds);
        if (selected == 0) Assert.Equal(DateTime.FromFileTimeUtc((long)Basic.Birth).ToString("O", CultureInfo.InvariantCulture), value.StringValue);
        if (selected == 1) Assert.Equal(api.Image, value.StringValue);
        if (selected == 2) Assert.Equal(MetricInteger.Parse("0"), value.IntegerValue);
        if (selected == 3) Assert.Equal(api.Sid, value.StringValue);
    }

    [Theory]
    [InlineData("open")] [InlineData("identity-before")] [InlineData("identity-after")]
    [InlineData("times-before")] [InlineData("times-after")] [InlineData("close")] [InlineData("throw-close")]
    public async Task FailedIdentityOrReleaseCannotPublishPathSidParentOrStartTime(string fault)
    {
        var api = new Basic.Api { Fault = fault }; using var fixture = Basic.Fixture(api);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Basic.Request(Names), default);
        Assert.All(sample.ProcessFields!.Processes.SelectMany(row => row.Values), value =>
        { Assert.Null(value.StringValue); Assert.Null(value.IntegerValue); Assert.Null(value.NumericValue); });
        if (fault is "open" or "identity-before" or "times-before")
            Assert.Equal(0, api.ImageCalls + api.SidCalls + api.ParentCalls);
        api.Fault = "";
        var recovered = await fixture.Sampler.CaptureSnapshotAsync(Basic.Request(Names), default);
        Assert.Equal(api.Image, Basic.Value(recovered, Names[1]).StringValue);
        Assert.Equal(api.Sid, Basic.Value(recovered, Names[3]).StringValue);
    }

    [Theory]
    [InlineData("image", 1)] [InlineData("throw-image", 1)]
    [InlineData("parent", 2)] [InlineData("throw-parent", 2)]
    [InlineData("sid", 3)] [InlineData("throw-sid", 3)]
    public async Task CategoryFailureIsExplicitlyEmptyWithoutDiscardingOtherSelectedFields(string fault, int empty)
    {
        var api = new Basic.Api { Fault = fault }; using var fixture = Basic.Fixture(api);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Basic.Request(Names), default);
        var value = Basic.Value(sample, Names[empty]); Assert.Null(value.StringValue); Assert.Null(value.IntegerValue);
        Assert.NotNull(Basic.Value(sample, Names[0]).StringValue);
        if (empty != 1) Assert.Equal(api.Image, Basic.Value(sample, Names[1]).StringValue);
        if (empty != 3) Assert.Equal(api.Sid, Basic.Value(sample, Names[3]).StringValue);
        Assert.Equal(1, api.Opens); Assert.Equal(2, api.TimeCalls);
    }

    [Fact]
    public async Task PublicDirectSubscriptionCacheAndClearKeepTextAndQueryWindowTyped()
    {
        var api = new Basic.Api { Parent = 42 }; using var fixture = Basic.Fixture(api);
        var backend = new HostProcessFieldBackend(fixture.Sampler); await backend.DiscoverAsync(default);
        var capabilities = Names.Select(name => Assert.Single(backend.Catalog.Fields, field => field.Field.FieldName == name)).ToArray();
        Assert.All(capabilities, field => { Assert.True(field.Read.Supported); Assert.False(field.Write.Supported); Assert.Equal("windows.process.query", field.ReadSource); });
        Assert.Equal("string", capabilities[0].Definition.ValueKind); Assert.Equal("unsigned64", capabilities[2].Definition.ValueKind);
        var references = capabilities.Select(field => field.Field).ToImmutableArray();
        Assert.Equal(1, api.Opens); // explicit DiscoverAsync query; catalog/cache access stays passive
        var opens = api.Opens;
        _ = backend.Catalog;
        foreach (var field in references) _ = backend.ReadCached(field);
        Assert.Equal(opens, api.Opens);

        await fixture.Sampler.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var direct = await backend.CollectAsync(references, timeout.Token);
            Assert.Equal(api.Image, direct[1].Value?.Text); Assert.Equal(MetricInteger.Parse("42"), direct[2].Value?.Integer); Assert.Equal(api.Sid, direct[3].Value?.Text);
            Assert.All(direct, reading => Assert.Null(reading.SampleDurationMilliseconds));
            Assert.Equal(1, api.Opens); // discovery populated fixed facts for this instance
            Assert.Equal(direct[1], backend.ReadCached(references[1]));
            Assert.Equal(direct[2], backend.ReadCached(references[2]));
            Assert.Equal(direct[3], backend.ReadCached(references[3]));
            Assert.Equal(1, api.Opens);

            using var subscriptionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = backend.SubscribeAsync("identity", references, TimeSpan.FromSeconds(1),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), subscriptionTimeout.Token)
                .GetAsyncEnumerator(subscriptionTimeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var fresh = stream.Current;
            Assert.Equal(references.ToArray(), fresh.Select(static reading => reading.Field).ToArray());
            Assert.Equal(api.Image, fresh[1].Value?.Text); Assert.Equal(MetricInteger.Parse("42"), fresh[2].Value?.Integer); Assert.Equal(api.Sid, fresh[3].Value?.Text);
            Assert.All(fresh, reading => Assert.Null(reading.SampleDurationMilliseconds));
            Assert.Equal(1, api.Opens); // a fresh trigger reuses this instance's fixed facts
            Assert.All(fresh, reading => Assert.Equal(reading, backend.ReadCached(reading.Field)));
            Assert.Equal(1, api.Opens);

            var opensAtCancellation = api.Opens;
            var pending = stream.MoveNextAsync().AsTask();
            subscriptionTimeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            Assert.Equal(opensAtCancellation, api.Opens);

            api.Fault = "identity-after";
            var retained = await backend.CollectAsync(references, timeout.Token);
            // These fixed facts are already established; this frame performs no failing query.
            Assert.All(retained, reading => { Assert.Equal(FieldObservationStatus.Current, reading.Status); Assert.NotNull(reading.Value); });
            Assert.Equal(api.Image, retained[1].Value?.Text);
            Assert.Equal(MetricInteger.Parse("42"), retained[2].Value?.Integer);
            Assert.Equal(api.Sid, retained[3].Value?.Text);
            Assert.Equal(opensAtCancellation, api.Opens);
            var captured = fixture.Sampler.ReadLatest(Basic.Request(Names)).ProcessFields!;
            Assert.All(captured.Clear().Processes.SelectMany(row => row.Values), value => Assert.Null(value.StringValue));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(captured.Clear(), Basic.WebJson));
            Assert.All(json.RootElement.GetProperty("processes")[0].GetProperty("values").EnumerateArray(), value =>
                Assert.Equal(JsonValueKind.Null, value.GetProperty("stringValue").ValueKind));
        }
        finally
        {
            await fixture.Sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MixedCpuIntervalAndIdentityShareCaptureWithoutLeakingWindowOrExtraCounters()
    {
        var api = new Basic.Api(); using var fixture = Basic.Fixture(api);
        var request = Basic.Request([.. Names, "process_all_cpu_usage_actual", "process_all_cpu_time_actual"]);
        await fixture.Sampler.CaptureSnapshotAsync(request, default);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Equal(0, Basic.Value(sample, "process_all_cpu_usage_actual").NumericValue);
        Assert.NotNull(sample.ProcessFields!.Select(new HashSet<string> { "process_all_cpu_usage_actual" }).WindowMilliseconds);
        Assert.Null(sample.ProcessFields.Select(Names.ToHashSet()).WindowMilliseconds);
        Assert.Equal(2, api.Opens); Assert.Equal(4, api.TimeCalls); Assert.Equal(1, api.ImageCalls); // fixed image is read once; CPU counters are read twice
        Assert.Equal(0, api.MemoryCalls + api.IoCalls + api.HandleCalls);
    }
}

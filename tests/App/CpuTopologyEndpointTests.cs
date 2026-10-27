using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyEndpointTests
{
    [Theory]
    [InlineData("/api/cpu/topology")]
    [InlineData("/api/cpu/topology/exclusive-bindings")]
    [InlineData("/api/cpu/topology/fields")]
    public async Task OrdinaryHttpReadsOwnDemandAndReturnExplicitUnavailableAfterAFailedCapture(string path)
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var app = await StartEndpointsAsync(fixture.Reader);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.GetAsync(path, timeout.Token);
        while (fixture.ReadDemand().SourceCount != 1) await Task.Delay(10, timeout.Token);
        Assert.False(pending.IsCompleted);
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(new InvalidOperationException("Recorded capture failure."));
        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (path != "/api/cpu/topology/fields")
        {
            using var unavailable = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("unavailable", unavailable.RootElement.GetProperty("state").GetString());
            Assert.Equal("cpu-topology-not-published", unavailable.RootElement.GetProperty("reasonCode").GetString());
        }
        else
        {
            using var fields = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, fields.RootElement.GetProperty("observedAt").ValueKind);
            Assert.Empty(fields.RootElement.GetProperty("devices").EnumerateArray());
        }
        Assert.Equal(1, fixture.Sampler.CaptureCount);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task ResidencyReadsExplainMissingWindowAndSummarizeTheSamePublishedWindow()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        CpuCoreResidencySnapshot? value = null;
        var residency = new RecordingCpuCoreResidencyReader(() => value);
        await using var app = await StartEndpointsAsync(fixture.Reader, residency);
        using var client = CreateClient(app);
        foreach (var path in new[] { "/api/cpu/residency", "/api/cpu/residency/cores" })
        {
            var empty = await client.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal("unavailable", empty.GetProperty("state").GetString());
            Assert.Equal("cpu-residency-window-unavailable", empty.GetProperty("reasonCode").GetString());
        }
        value = CpuCoreResidencyTestValues.Create(19, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(1, 10, ("core:0", 20), ("core:1", 10)),
            CpuCoreResidencyTestValues.Process(2, 20, ("core:0", 30))) with
            { LogicalProcessorScopes = [new(0, "core:0", "ccd:0", "fixture"), new(1, "core:2", "ccd:0", "fixture")] };
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/cpu/residency/cores");
        Assert.Equal(value.MeasuredFrom, summary.GetProperty("measuredFrom").GetDateTimeOffset());
        Assert.Equal(value.MeasuredThrough, summary.GetProperty("measuredThrough").GetDateTimeOffset());
        var cores = summary.GetProperty("cores").EnumerateArray().ToArray();
        Assert.Equal(50, cores.Single(core => core.GetProperty("physicalCoreId").GetString() == "core:0").GetProperty("usagePercent").GetDouble());
        Assert.Equal(0, cores.Single(core => core.GetProperty("physicalCoreId").GetString() == "core:2").GetProperty("usagePercent").GetDouble());
        Assert.Empty(residency.Subscriptions);
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task SharedHttpChannelForwardsNullAndRecoveryWithoutEndingEitherSubscription()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var app = await StartEndpointsAsync(fixture.Reader);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions = new[]
                {
                    new { id = "cpu-first", path = "/api/cpu/topology/subscribe?intervalMs=100" },
                    new { id = "cpu-second", path = "/api/cpu/topology/fields/subscribe?intervalMs=100" }
                }
            })
        };
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        while (fixture.ReadDemand().SourceCount != 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        Assert.Equal(2u, fixture.ReadDemand().SourceCount);
        fixture.Sampler.Complete(new InvalidOperationException("Native capture failed."));

        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var emptyFrames = await ReadPairAsync(stream, timeout.Token);
        Assert.Equal(JsonValueKind.Null, Assert.Single(emptyFrames, frame =>
            frame.GetProperty("subscriptionId").GetString() == "cpu-first").GetProperty("value").ValueKind);
        var emptyFields = Assert.Single(emptyFrames, frame =>
            frame.GetProperty("subscriptionId").GetString() == "cpu-second").GetProperty("value");
        Assert.Equal(JsonValueKind.Null, emptyFields.GetProperty("observedAt").ValueKind);
        Assert.Empty(emptyFields.GetProperty("devices").EnumerateArray());

        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        var xstate = CpuThreadCpuidInformationTests.CapabilitiesSnapshot().OsTopologyInformation!;
        var snapshot = CpuTopologyProviderTestFixture.CreateSnapshot(34) with
        { OsTopologyInformation = CpuTopologyInformationFieldTests.Snapshot().OsTopologyInformation! with
            {
                NumaNodes = [new(7, [135])], ThreadCpuid = xstate.ThreadCpuid,
                Packages = CpuPackageInformationTests.SnapshotWithPackages().OsTopologyInformation!.Packages,
                OsUsableFeatures = xstate.OsUsableFeatures, OsEnabledXStateMask = xstate.OsEnabledXStateMask
            } };
        fixture.Sampler.Complete(snapshot);
        var populatedFrames = await ReadPairAsync(stream, timeout.Token);
        Assert.All(populatedFrames, frame =>
        {
            Assert.Equal(["subscriptionId", "value"], frame.EnumerateObject().Select(property => property.Name));
        });
        var legacy = Assert.Single(populatedFrames, frame => frame.GetProperty("subscriptionId").GetString() == "cpu-first")
            .GetProperty("value");
        Assert.Equal("Subscription Test CPU", legacy.GetProperty("cpuName").GetString());
        Assert.False(legacy.TryGetProperty("notes", out _));
        Assert.All(legacy.GetProperty("ccds").EnumerateArray(), ccd => Assert.False(ccd.TryGetProperty("label", out _)));
        Assert.All(legacy.GetProperty("physicalCores").EnumerateArray(), core => Assert.False(core.TryGetProperty("label", out _)));
        Assert.Equal(34, legacy.GetProperty("logicalProcessors")[0].GetProperty("usagePercent").GetDouble());
        var information = Assert.Single(populatedFrames, frame => frame.GetProperty("subscriptionId").GetString() == "cpu-second")
            .GetProperty("value");
        Assert.Equal(snapshot.CapturedAt, information.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(20d, FieldValue(information, "windows-cpu-thread|0:1", "cpu_all_logical_busy_ratio").GetDouble());
        Assert.Equal(50d, FieldValue(information, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").GetDouble());
        Assert.Equal(37.123456789, FieldValue(information, "windows-cpu-thread|2:7", "cpu_all_logical_busy_ratio").GetDouble());
        Assert.Equal(11, information.GetProperty("devices").GetArrayLength());
        Assert.Equal("18446744073709551615", FieldValue(information, "windows-cpu-thread|0:1", "cpu_all_supported_xcr0_state_mask").GetString());
        Assert.Equal("18446744073709551615", FieldValue(information, "windows-cpu-thread|0:1", "cpu_intel_supported_xss_state_mask").GetString());
        Assert.Equal("4294967295", FieldValue(information, "windows-cpu-thread|2:7", "cpu_amd_supported_xss_state_mask").GetString());
        Assert.Equal("18446744073709551615", FieldValue(information, "windows-cpu-os-features", "cpu_all_os_enabled_xstate_mask").GetString());
        Assert.Equal("2", FieldValue(information, "windows-cpu-thread|0:1", "cpu_intel_avx10_version").GetString());
        Assert.Equal("2", FieldValue(information, "windows-cpu-topology", "cpu_all_package_count_reported").GetString());
        var packageAddress = FieldValue(information, "windows-cpu-package|2:7", "cpu_all_package_logical_processor_addresses")[0];
        Assert.Equal(2, packageAddress.GetProperty("processorGroup").GetInt32());
        Assert.Equal(7, packageAddress.GetProperty("groupRelativeIndex").GetInt32());
        Assert.Contains("AVX10", FieldValue(information, "windows-cpu-thread|0:1", "cpu_all_instruction_feature")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("FMA4", FieldValue(information, "windows-cpu-thread|2:7", "cpu_all_instruction_feature")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("52", FieldValue(information, "windows-cpu-thread|0:1", "cpu_all_physical_address_width_declared_bits").GetString());
        Assert.True(FieldValue(information, "windows-cpu-thread|0:1", "cpu_intel_vmx_supported").GetBoolean());
        Assert.True(FieldValue(information, "windows-cpu-thread|2:7", "cpu_amd_svm_supported").GetBoolean());
        Assert.Equal("4294967295", FieldValue(information, "windows-cpu-thread|2:7", "cpu_amd_svm_asid_count_declared").GetString());
        Assert.Equal("255", FieldValue(information, "windows-cpu-thread|2:7", "cpu_amd_svm_revision").GetString());
        Assert.Contains("x2AVIC_EXT", FieldValue(information, "windows-cpu-thread|2:7", "cpu_amd_svm_feature")
            .EnumerateArray().Select(value => value.GetString()));
        var node = Assert.Single(information.GetProperty("devices").EnumerateArray(),
            device => device.GetProperty("deviceIdentity").GetString() == "windows-numa-node|7");
        Assert.Equal(2, node.GetProperty("fields").GetArrayLength());
        var getResponseTask = client.GetAsync("/api/cpu/topology/fields", timeout.Token);
        while (fixture.ReadDemand().SourceCount != 3) await Task.Delay(10, timeout.Token);
        Assert.False(getResponseTask.IsCompleted);
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        var freshSnapshot = snapshot with { CapturedAt = DateTimeOffset.UtcNow };
        fixture.Sampler.Complete(freshSnapshot);
        using var getResponse = await getResponseTask;
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var freshFields = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(freshSnapshot.CapturedAt, freshFields.RootElement.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(information.GetProperty("devices").GetRawText(), freshFields.RootElement.GetProperty("devices").GetRawText());
        _ = await ReadPairAsync(stream, timeout.Token);
        Assert.Equal(4, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(new InvalidOperationException("Native capture failed after publishing XState."));
        var failedFrames = await ReadPairAsync(stream, timeout.Token);
        Assert.Equal(JsonValueKind.Null, Assert.Single(failedFrames, frame =>
            frame.GetProperty("subscriptionId").GetString() == "cpu-first").GetProperty("value").ValueKind);
        var failedFields = Assert.Single(failedFrames, frame =>
            frame.GetProperty("subscriptionId").GetString() == "cpu-second").GetProperty("value");
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_all_supported_xcr0_state_mask").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_all_logical_busy_ratio").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-core|1,3", "cpu_all_physical_core_busy_ratio").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_intel_supported_xss_state_mask").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|2:7", "cpu_amd_supported_xss_state_mask").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-os-features", "cpu_all_os_enabled_xstate_mask").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_intel_avx10_version").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-topology", "cpu_all_package_count_reported").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-package|2:7", "cpu_all_package_logical_processor_addresses").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_all_instruction_feature").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|2:7", "cpu_all_instruction_feature").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_all_physical_address_width_declared_bits").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|0:1", "cpu_intel_vmx_supported").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|2:7", "cpu_amd_svm_supported").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|2:7", "cpu_amd_svm_asid_count_declared").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|2:7", "cpu_amd_svm_revision").ValueKind);
        Assert.Equal(JsonValueKind.Null, FieldValue(failedFields, "windows-cpu-thread|2:7", "cpu_amd_svm_feature").ValueKind);
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync().WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    private static JsonElement FieldValue(JsonElement snapshot, string deviceIdentity, string fieldName)
    {
        var device = Assert.Single(snapshot.GetProperty("devices").EnumerateArray(),
            row => row.GetProperty("deviceIdentity").GetString() == deviceIdentity);
        var field = Assert.Single(device.GetProperty("fields").EnumerateArray(),
            row => row.GetProperty("definition").GetProperty("name").GetString() == fieldName);
        return field.GetProperty("value");
    }

    private static async Task<JsonElement[]> ReadPairAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        var frames = new JsonElement[2];
        for (var index = 0; index < frames.Length; index++)
        {
            using var document = JsonDocument.Parse((await stream.ReadLineAsync(cancellationToken))!);
            frames[index] = document.RootElement.Clone();
        }
        Assert.Equal(["cpu-first", "cpu-second"], frames
            .Select(frame => frame.GetProperty("subscriptionId").GetString()).Order());
        return frames;
    }

    private static async Task<WebApplication> StartEndpointsAsync(ICpuTopologyReader reader, ICpuCoreResidencyReader? residency = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "read", "processes.read");
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton(reader);
        if (residency is not null) builder.Services.AddSingleton(residency);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        var endpoints = typeof(ResourceManagerEndpointRouteBuilderExtensions);
        endpoints.GetMethod("MapSystemEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, StartupCapabilitySet.NormalReadOnly]);
        endpoints.GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
        => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient()
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = CpuTopologyProviderTestFixture.Deadline
        }, app.Services);
}

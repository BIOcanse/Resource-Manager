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
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyEndpointTests
{
    [Fact]
    public async Task OrdinaryHttpReadsReturnJsonNullWithoutCapturing()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var app = await StartEndpointsAsync(fixture.Reader);
        using var client = CreateClient(app);
        foreach (var path in new[] { "/api/cpu/topology", "/api/cpu/topology/exclusive-bindings" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("null", await response.Content.ReadAsStringAsync());
        }
        using var fieldsResponse = await client.GetAsync("/api/cpu/topology/fields");
        Assert.Equal(HttpStatusCode.OK, fieldsResponse.StatusCode);
        using var fields = JsonDocument.Parse(await fieldsResponse.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, fields.RootElement.GetProperty("observedAt").ValueKind);
        Assert.Empty(fields.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
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
        using var getResponse = await client.GetAsync("/api/cpu/topology/fields", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var cached = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(information.GetRawText(), cached.RootElement.GetRawText());
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
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

    private static async Task<WebApplication> StartEndpointsAsync(ICpuTopologyReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton(reader);
        var app = builder.Build();
        var endpoints = typeof(ResourceManagerEndpointRouteBuilderExtensions);
        endpoints.GetMethod("MapSystemEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, StartupCapabilitySet.NormalReadOnly]);
        endpoints.GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
        => new()
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = CpuTopologyProviderTestFixture.Deadline
        };
}

using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class NativeBuildPolicyDeclarationTests
{
    [Fact]
    public void GeneratedNativeDefaultsAndManagedOwnersUseTheSameSelectedDeclaration()
    {
        var selected = NativeBuildPolicySettings.ProductDefaults;
        Assert.Equal(selected, HostManagerTestPlanFactory.CreatePlan().NativeBuildPolicy);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(selected));
        var fields = typeof(NativeKernelEtwSessionConfig).Assembly
            .GetType("ResourceManager.App.Infrastructure.Telemetry.Etw.NativePolicyDefaults", throwOnError: true)!
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic).Where(static field => field.IsLiteral).ToArray();
        Assert.Equal(json.RootElement.EnumerateObject().Count(), fields.Length);
        foreach (var field in fields)
            Assert.Equal(json.RootElement.GetProperty(field.Name).GetUInt32(), field.GetRawConstantValue());
    }

    [Fact]
    public void BuildPolicyOverridesArePublishedAndChangeTheBuildDigest()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.NativeBuildPolicy)["value"]!;
            value["maximum_network_interfaces"] = 128;
            value["data_area_borrow_drain_timeout_ms"] = 0;
            value["runtime_rank_tray"] = 7;
        });
        Assert.Equal(128u, changed.NativeBuildPolicy!.MaximumNetworkInterfaces);
        Assert.Equal(0u, changed.NativeBuildPolicy.DataAreaBorrowDrainTimeoutMilliseconds);
        Assert.Equal(7u, changed.NativeBuildPolicy.RuntimeRankTray);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
    }

    [Theory]
    [InlineData("maximum_network_interfaces", 0)]
    [InlineData("etw_allocator_requested_divisor", 0)]
    [InlineData("interrupt_growth_factor", 1)]
    [InlineData("residency_ring_chunk_bytes", 1)]
    public void InvalidNativePolicyFailsBeforeOwnersStart(string field, int value)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.NativeBuildPolicy)["value"]![field] = value));
}

using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Fact]
    public async Task CpuidUsesOriginalOwnerAtomicCacheAndClearsFailedProcessorInTheNextCapture()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        using var cancellation = new CancellationTokenSource();
        await using var subscription = fixture.Reader.SubscribeInformationAsync("cpuid", TimeSpan.FromMilliseconds(100),
            cancellation.Token).GetAsyncEnumerator();
        var first = subscription.MoveNextAsync().AsTask();
        // Drive captures explicitly: cache assertions must not race a periodic capture.
        var capture = Task.Run(async () => await fixture.Reader.CaptureInformationAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        var snapshot = InstructionSnapshot(); fixture.Sampler.Complete(snapshot);
        await capture.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.True(await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        var information = subscription.Current;
        Assert.Same(snapshot, fixture.Reader.GetSnapshot());
        for (var index = 0; index < 10; index++) Assert.Same(information, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(1, fixture.Sampler.CaptureCount);
        Assert.Equal(1u, fixture.ReadDemand().ItemCount);
        foreach (var name in new[] { "USER_MSR", "APX_F" })
            Assert.Contains(name, Field(Device(information, "windows-cpu-thread|0:1"), "cpu_all_instruction_feature")
                .Value!.Value.EnumerateArray().Select(item => item.GetString()));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(information, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var intel = payload.RootElement.GetProperty("devices").EnumerateArray()
            .Single(device => device.GetProperty("deviceIdentity").GetString() == "windows-cpu-thread|0:1");
        var raw = intel.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("definition").GetProperty("name").GetString() == "cpu_all_cpuid_identity_registers");
        Assert.Equal(uint.MaxValue, raw.GetProperty("value").EnumerateArray()
            .Single(row => row.GetProperty("leaf").GetUInt32() == 0x1F).GetProperty("edx").GetUInt32());

        var next = subscription.MoveNextAsync().AsTask();
        capture = Task.Run(async () => await fixture.Reader.CaptureInformationAsync(CancellationToken.None));
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        Assert.Same(information, fixture.Reader.GetInformationSnapshot());
        var failed = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { ThreadCpuid = [new(1, null), snapshot.OsTopologyInformation.ThreadCpuid![1]] } };
        fixture.Sampler.Complete(failed);
        await capture.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Null(Field(Device(subscription.Current, "windows-cpu-thread|0:1"), "cpu_all_cpuid_vendor").Value);
        Assert.Null(Field(Device(subscription.Current, "windows-cpu-thread|0:1"), "cpu_all_instruction_feature").Value);
        Assert.NotNull(Field(Device(subscription.Current, "windows-cpu-thread|2:7"), "cpu_amd_extended_apic_id").Value);
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
    }
}

using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuThreadCpuidInformationTests
{
    [Theory]
    [InlineData("GenuineIntel")] [InlineData("AuthenticAMD")]
    public void XstateKeepsCommon64BitXcr0AndVendorSpecificXssWidth(string vendor)
    {
        var reader = XStateRegisters(vendor);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(ulong.MaxValue, data.SupportedXcr0StateMask);
        Assert.Equal(vendor == "GenuineIntel" ? ulong.MaxValue : (ulong?)null, data.IntelSupportedXssStateMask);
        Assert.Equal(vendor == "AuthenticAMD" ? uint.MaxValue : (uint?)null, data.AmdSupportedXssStateMask);
        Assert.All(reader.Calls, pair => Assert.Equal(1, pair.Value));
        Assert.Equal(uint.MaxValue, data.IdentityRegisters.Single(row => row.Leaf == 0xD && row.Subleaf == 1).Edx);
        Assert.Empty(data.Identity.OsUsableFeatures); // Hardware XSAVE support does not imply OS support.
    }

    [Theory]
    [InlineData("GenuineIntel", 12U, true)]
    [InlineData("GenuineIntel", 13U, false)]
    [InlineData("AuthenticAMD", 12U, true)]
    [InlineData("AuthenticAMD", 13U, false)]
    [InlineData("CentaurHauls", 31U, true)]
    public void UnsupportedLeafXsaveOrUnknownVendorNeverQueriesTheXstateLeaves(string vendor, uint maximum, bool xsave)
    {
        var reader = XStateRegisters(vendor, maximum);
        if (!xsave) reader.Values[(1, 0)] = reader.Values[(1, 0)] with { Item3 = 0 };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Null(data.SupportedXcr0StateMask); Assert.Null(data.IntelSupportedXssStateMask); Assert.Null(data.AmdSupportedXssStateMask);
        Assert.DoesNotContain(reader.Calls.Keys, key => key.Item1 == 0xD);
    }

    [Theory]
    [InlineData("GenuineIntel")] [InlineData("AuthenticAMD")]
    public void XssRequiresXsavesWhileSupportedZeroMasksRemainReported(string vendor)
    {
        var reader = XStateRegisters(vendor);
        reader.Values[(0xD, 0)] = (0, 0, 0, 0);
        reader.Values[(0xD, 1)] = (0, 0, -1, -1);
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        Assert.Equal(0UL, data.SupportedXcr0StateMask);
        Assert.Null(data.IntelSupportedXssStateMask); Assert.Null(data.AmdSupportedXssStateMask);
        reader.Values[(0xD, 1)] = (8, 0, 0, 0);
        data = WindowsCpuThreadCpuidReader.ReadRegisters(reader.Read);
        if (vendor == "GenuineIntel") Assert.Equal(0UL, data.IntelSupportedXssStateMask);
        else Assert.Equal(0U, data.AmdSupportedXssStateMask);
    }

    [Fact]
    public void OsEnabledStatePreservesZeroAndHighBitsAndApiAbsenceIsEmpty()
    {
        Assert.Equal(0UL, WindowsCpuOsFeatureReader.ReadEnabledStateMask(() => 0));
        Assert.Equal(ulong.MaxValue, WindowsCpuOsFeatureReader.ReadEnabledStateMask(() => ulong.MaxValue));
        Assert.Null(WindowsCpuOsFeatureReader.ReadEnabledStateMask(() => throw new EntryPointNotFoundException()));
        Assert.Null(WindowsCpuOsFeatureReader.ReadEnabledStateMask(() => throw new DllNotFoundException()));
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsFeatureReader.ReadEnabledStateMask(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void TypedXstateProjectionKeepsAllBitsAndIndependentOsAndHardwareFailure()
    {
        var snapshot = XStateSnapshot();
        var current = CpuTopologyInformationProjection.Create(snapshot, DeviceInformationSnapshot.Empty);
        var intel = Device(current, "windows-cpu-thread|0:1");
        var amd = Device(current, "windows-cpu-thread|2:7");
        Assert.Equal(ulong.MaxValue.ToString(), Field(intel, "cpu_all_supported_xcr0_state_mask").Value!.Value.GetString());
        Assert.Equal(ulong.MaxValue.ToString(), Field(intel, "cpu_intel_supported_xss_state_mask").Value!.Value.GetString());
        Assert.Equal("bitmask32", Field(amd, "cpu_amd_supported_xss_state_mask").Definition.Unit);
        Assert.Equal(uint.MaxValue.ToString(), Field(amd, "cpu_amd_supported_xss_state_mask").Value!.Value.GetString());
        Assert.DoesNotContain(amd.Fields, field => field.Definition.Vendor == "intel");
        Assert.DoesNotContain(intel.Fields, field => field.Definition.Vendor == "amd");
        var partial = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { OsUsableFeatures = null, OsEnabledXStateMask = 0, ThreadCpuid = [new(1, null), snapshot.OsTopologyInformation.ThreadCpuid![1]] } };
        var empty = CpuTopologyInformationProjection.Create(partial, current);
        Assert.Null(Field(Device(empty, "windows-cpu-thread|0:1"), "cpu_all_supported_xcr0_state_mask").Value);
        Assert.NotNull(Field(Device(empty, "windows-cpu-thread|2:7"), "cpu_amd_supported_xss_state_mask").Value);
        Assert.Equal("0", Field(Device(empty, "windows-cpu-os-features"), "cpu_all_os_enabled_xstate_mask").Value!.Value.GetString());
        Assert.Null(Field(Device(empty, "windows-cpu-os-features"), "cpu_all_os_usable_feature").Value);
        partial = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with { OsEnabledXStateMask = null } };
        empty = CpuTopologyInformationProjection.Create(partial, current);
        Assert.Null(Field(Device(empty, "windows-cpu-os-features"), "cpu_all_os_enabled_xstate_mask").Value);
        Assert.NotNull(Field(Device(empty, "windows-cpu-os-features"), "cpu_all_os_usable_feature").Value);
        Assert.NotNull(Field(Device(empty, "windows-cpu-thread|0:1"), "cpu_all_supported_xcr0_state_mask").Value);
        Assert.Contains("\"value\":\"18446744073709551615\"", JsonSerializer.Serialize(current, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task XstateUsesOriginalCpuOwnerCacheAndUnifiedInformationSubscription()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Reader.SubscribeInformationAsync("xstate", TimeSpan.FromSeconds(5),
            CancellationToken.None).GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        // Drive the original capture owner without racing the subscription's next due time.
        var capture = Task.Run(async () => await fixture.Reader.CaptureInformationAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        fixture.Sampler.Complete(XStateSnapshot());
        await capture.WaitAsync(CpuTopologyProviderTestFixture.Deadline);
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Same(subscription.Current, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(ulong.MaxValue.ToString(), Field(Device(subscription.Current, "windows-cpu-os-features"), "cpu_all_os_enabled_xstate_mask").Value!.Value.GetString());
        Assert.Equal(ulong.MaxValue.ToString(), Field(Device(subscription.Current, "windows-cpu-thread|0:1"), "cpu_intel_supported_xss_state_mask").Value!.Value.GetString());
        Assert.Equal(1, fixture.Sampler.CaptureCount); Assert.Equal(1u, fixture.ReadDemand().ItemCount);
    }

    private static RegisterReader XStateRegisters(string vendor, uint maximum = 0x1F)
    {
        var reader = Registers(vendor, maximum);
        var identity = reader.Values[(1, 0)];
        reader.Values[(1, 0)] = (identity.Item1, identity.Item2, identity.Item3 | 1 << 26, identity.Item4);
        reader.Values[(0xD, 0)] = (-1, 0, 0, -1);
        reader.Values[(0xD, 1)] = (8, 0, -1, -1);
        return reader;
    }

    internal static CpuTopologySnapshot XStateSnapshot()
    {
        var snapshot = Snapshot();
        return snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
        {
            ThreadCpuid = [new(1, WindowsCpuThreadCpuidReader.ReadRegisters(XStateRegisters("GenuineIntel").Read)),
                new(135, WindowsCpuThreadCpuidReader.ReadRegisters(XStateRegisters("AuthenticAMD").Read))],
            OsEnabledXStateMask = ulong.MaxValue
        } };
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class NativeSoftwareIdentityAbiTests
{
    [Fact]
    public void CatalogAbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0005_0000U, NativeSoftwareIdentityCatalogAbi.Version);
        Assert.Equal(224, Unsafe.SizeOf<NativeSoftwareIdentityCatalogConfiguration>());
        Assert.Equal(80, Unsafe.SizeOf<NativeSoftwareIdentityCatalogCapacity>());
        Assert.Equal(72, Unsafe.SizeOf<NativeSoftwareIdentityCatalogEntryInput>());
        Assert.Equal(56, Unsafe.SizeOf<NativeSoftwareIdentityCatalogAliasInput>());
        Assert.Equal(56, Unsafe.SizeOf<NativeSoftwareIdentityCatalogRootInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativeSoftwareIdentityCatalogReplaceInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativeSoftwareIdentityCatalogQueryInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativeSoftwareIdentityCatalogFactInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativeSoftwareIdentityKnownQueryInput>());
        Assert.Equal(40, Unsafe.SizeOf<NativeSoftwareIdentityKnownSignalInput>());
        Assert.Equal(128, Unsafe.SizeOf<NativeSoftwareIdentityCatalogMatchOutput>());
        Assert.Equal(168, Unsafe.SizeOf<NativeSoftwareIdentityKnownMatchOutput>());
        Assert.Equal(128, Unsafe.SizeOf<NativeSoftwareIdentityCatalogSummary>());

        AssertOffset<NativeSoftwareIdentityCatalogConfiguration>(
            nameof(NativeSoftwareIdentityCatalogConfiguration.Generation),
            8);
        AssertOffset<NativeSoftwareIdentityCatalogConfiguration>(
            nameof(NativeSoftwareIdentityCatalogConfiguration.EntryIndexCapacity),
            44);
        AssertOffset<NativeSoftwareIdentityCatalogConfiguration>(
            nameof(NativeSoftwareIdentityCatalogConfiguration.ResidentByteBudget),
            80);
        AssertOffset<NativeSoftwareIdentityCatalogConfiguration>(
            nameof(NativeSoftwareIdentityCatalogConfiguration.Flags),
            184);
        AssertOffset<NativeSoftwareIdentityCatalogReplaceInput>(
            nameof(NativeSoftwareIdentityCatalogReplaceInput.ValidMask),
            64);
        AssertOffset<NativeSoftwareIdentityCatalogQueryInput>(
            nameof(NativeSoftwareIdentityCatalogQueryInput.ValidMask),
            48);
        AssertOffset<NativeSoftwareIdentityKnownQueryInput>(
            nameof(NativeSoftwareIdentityKnownQueryInput.ValidMask),
            48);
        AssertOffset<NativeSoftwareIdentityKnownMatchOutput>(
            nameof(NativeSoftwareIdentityKnownMatchOutput.MatchedRootHandle),
            96);
        AssertOffset<NativeSoftwareIdentityKnownMatchOutput>(
            nameof(NativeSoftwareIdentityKnownMatchOutput.DerivedIdentityFingerprintLow),
            112);
    }

    [Fact]
    public void ResolutionAbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0001_0000U, NativeSoftwareIdentityResolutionAbi.Version);
        Assert.Equal(104, Unsafe.SizeOf<NativeSoftwareIdentityResolutionConfiguration>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSoftwareIdentityResolutionCapacity>());
        Assert.Equal(48, Unsafe.SizeOf<NativeSoftwareIdentitySourcePolicyInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativeSoftwareIdentityPolicyReplaceInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativeSoftwareIdentityResolveInput>());
        Assert.Equal(80, Unsafe.SizeOf<NativeSoftwareIdentityObservationInput>());
        Assert.Equal(176, Unsafe.SizeOf<NativeSoftwareIdentityResolutionOutput>());
        Assert.Equal(128, Unsafe.SizeOf<NativeSoftwareIdentityResolutionSummary>());

        AssertOffset<NativeSoftwareIdentityResolutionConfiguration>(
            nameof(NativeSoftwareIdentityResolutionConfiguration.Generation),
            8);
        AssertOffset<NativeSoftwareIdentityResolutionConfiguration>(
            nameof(NativeSoftwareIdentityResolutionConfiguration.RequiredSourceMask),
            32);
        AssertOffset<NativeSoftwareIdentityResolutionConfiguration>(
            nameof(NativeSoftwareIdentityResolutionConfiguration.ResidentByteBudget),
            56);
        AssertOffset<NativeSoftwareIdentityPolicyReplaceInput>(
            nameof(NativeSoftwareIdentityPolicyReplaceInput.ValidMask),
            40);
        AssertOffset<NativeSoftwareIdentityResolveInput>(
            nameof(NativeSoftwareIdentityResolveInput.CommandUtcMilliseconds),
            32);
        AssertOffset<NativeSoftwareIdentityResolveInput>(
            nameof(NativeSoftwareIdentityResolveInput.ValidMask),
            56);
        AssertOffset<NativeSoftwareIdentityObservationInput>(
            nameof(NativeSoftwareIdentityObservationInput.ObservationGeneration),
            56);
        AssertOffset<NativeSoftwareIdentityResolutionOutput>(
            nameof(NativeSoftwareIdentityResolutionOutput.SelectedSourceId),
            56);
        AssertOffset<NativeSoftwareIdentityResolutionOutput>(
            nameof(NativeSoftwareIdentityResolutionOutput.ObservedSourceMask),
            112);
    }

    [Fact]
    public void RootDllExportsBothSoftwareIdentitySessions()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(
            NativeSoftwareIdentityCatalogAbi.Version,
            NativeSoftwareIdentityCatalogSession.GetAbiVersion());
        Assert.Equal(
            NativeSoftwareIdentityResolutionAbi.Version,
            NativeSoftwareIdentityResolutionSession.GetAbiVersion());

        using var catalog = new NativeSoftwareIdentityCatalogWorkspace(
            plan.SoftwareIdentityCatalog);
        using var resolution = new NativeSoftwareIdentityResolutionWorkspace(
            plan.SoftwareIdentityResolution);
        Assert.Equal(
            (uint)plan.SoftwareIdentityCatalog.Recreate.Capacity.MaximumEntryCount,
            catalog.Session.Capacity.EntryCapacity);
        Assert.Equal(
            (uint)plan.SoftwareIdentityResolution.Recreate.Capacity.MaximumPolicyCount,
            resolution.Session.Capacity.PolicyCapacity);
        Assert.Equal(
            plan.SoftwareIdentityResolution.ConfigurationGeneration,
            resolution.PolicyGeneration);
    }

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}

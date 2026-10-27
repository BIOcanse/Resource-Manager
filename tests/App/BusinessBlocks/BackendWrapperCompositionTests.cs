using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

public sealed class BackendWrapperCompositionTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public async Task ProductionCompositionBindsExactlyTheNativeProfilesWrappers(uint profile)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        services.AddResourceManagerApp([], profile == 3 ? StartupCapabilitySet.NormalReadOnly : StartupCapabilitySet.Full,
            BackendHostEnvironment.Compile(profile == 2, profile == 2));
        var business = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        Assert.DoesNotContain(business, value => BackendExpectedProductionTree.Wrappers.Contains(value.Id));
        var profileUids = NativeControl.GetProfileHostBlockUids(profile);
        var completed = BackendNativeControlFactory.WithWrappers(business, profileUids);
        Assert.True(profileUids.ToHashSet().SetEquals(completed.Select(value => value.Id)));
        var expectedWrappers = profileUids.Intersect(BackendExpectedProductionTree.Wrappers).ToHashSet();
        Assert.Equal(profile == 3 ? 6 : 9, expectedWrappers.Count);
        Assert.True(expectedWrappers.SetEquals(completed.Where(value =>
            BackendExpectedProductionTree.Wrappers.Contains(value.Id)).Select(value => value.Id)));

        // The scheduler's graph binds wrappers without activating business factories.
        var lazyBusiness = business.Select(value => value with
        {
            Create = _ => throw new InvalidOperationException("Metadata must not construct a business entry.")
        }).ToArray();
        await using var native = NativeBackendBlockTestSupport.CreateControl(lazyBusiness, profile);
        var graph = new BackendBlockGraph(lazyBusiness, native.Graph);
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.True(profileUids.ToHashSet().SetEquals(graph.BlockIds));
        foreach (var uid in expectedWrappers)
            Assert.IsType<BackendDomainBlock>(graph.Get(uid).Create(provider));
        Assert.All(lazyBusiness, value => Assert.Same(value, graph.Get(value.Id)));
    }

    [Fact]
    public void UnknownReportMemberDoesNotRequireAKnownCoordinatorToBindReports()
    {
        const int newReportMember = 9001;
        BusinessBlockRegistration[] business = [new(newReportMember, _ => throw new NotSupportedException())];
        var completed = BackendNativeControlFactory.WithWrappers(business, [BlockUids.Reports, newReportMember]);
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(2, completed.Length);
        Assert.Same(business[0], Assert.Single(completed, value => value.Id == newReportMember));
        Assert.IsType<BackendDomainBlock>(Assert.Single(completed, value => value.Id == BlockUids.Reports).Create(provider));
        Assert.DoesNotContain(completed, value => value.Id == BlockUids.HostManagerReportCoordinatorOwner);
        Assert.Equal([BlockUids.Reports], BackendNativeControlFactory.WithWrappers([], [BlockUids.Reports, newReportMember])
            .Select(value => value.Id));
        Assert.Same(business[0], Assert.Single(BackendNativeControlFactory.WithWrappers(business, [])));
    }

    [Fact]
    public void ExplicitFullTreeWrapperFactoriesRemainBound()
    {
        var explicitEntry = new BackendDomainBlock();
        BusinessBlockRegistration[] registrations = [new(BlockUids.Reports, _ => explicitEntry)];
        var completed = BackendNativeControlFactory.WithWrappers(registrations, [BlockUids.Reports]);
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Same(registrations[0], Assert.Single(completed));
        Assert.Same(explicitEntry, completed[0].Create(provider));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void CompletingWrappersStillRejectsMissingOrUnknownBusinessBindings(uint profile)
    {
        var business = NativeControl.GetProfileHostBlockUids(profile)
            .Except(BackendExpectedProductionTree.Wrappers)
            .Select(uid => new BusinessBlockRegistration(uid, _ => throw new NotSupportedException())).ToArray();
        Assert.Throws<ArgumentException>(() => NativeBackendBlockTestSupport.CreateControl(business.Skip(1), profile));
        Assert.Throws<ArgumentException>(() => NativeBackendBlockTestSupport.CreateControl(
            business.Append(new BusinessBlockRegistration(9001, _ => throw new NotSupportedException())), profile));
    }
}

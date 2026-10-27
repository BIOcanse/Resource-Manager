using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed partial class MetricCatalogPushTests
{
    [Fact]
    public void CatalogRevisionCoversAllPublicContentAndIgnoresDiscoveryGenerationsAndNumbers()
    {
        var catalog = new HardwareMetricCatalogState();
        var original = Snapshot("available", selected: true);
        var changes = 0;
        catalog.Changed += () => changes++;
        catalog.PublishCatalogProbe(original);
        var first = catalog.ReadPublication();
        for (var index = 0; index < 20; index++)
            catalog.PublishCatalogProbe(original with { WorkspaceIdentity = (ulong)index,
                ConfigurationGeneration = (ulong)index, CatalogGeneration = (ulong)index,
                Items = new Dictionary<string, MetricReadingValue> { ["cpu.usage"] = new("cpu.usage", index, "%") } });
        Assert.Same(first, catalog.ReadPublication());
        Assert.Equal(1, changes);
        var binding = original.FieldBindings.Single();
        catalog.PublishCatalogProbe(original with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
        { [binding.Key] = binding.Value with { Field = binding.Value.Field with { Unit = "changed unit" } } } });
        var second = catalog.ReadPublication();
        Assert.Equal(2, changes);
        Assert.NotEqual(first!.Revision, second!.Revision);
        Assert.Equal("changed unit", Assert.Single(Assert.Single(second.Fields).Fields).Unit);
        Assert.Equal("°C", Assert.Single(Assert.Single(first.Fields).Fields).Unit);
    }
    [Fact]
    public async Task OwnerSubscriptionDeliversCommittedValuesOnceAndRetainsContentForItsRevision()
    {
        var catalog = new HardwareMetricCatalogState();
        var original = Snapshot("unverified", selected: false);
        catalog.PublishCatalogProbe(original);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var values = catalog.SubscribeChangesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await values.MoveNextAsync());
        var first = values.Current;
        var next = values.MoveNextAsync().AsTask();
        for (var index = 0; index < 20; index++) catalog.PublishCatalogProbe(original);
        Assert.False(next.IsCompleted);
        catalog.PublishCatalogProbe(Snapshot("available", selected: true));
        Assert.True(await next);
        Assert.NotEqual(first.Revision, values.Current.Revision);
        Assert.Equal("unverified", Assert.Single(Assert.Single(first.Fields).Fields).Reading!.SourceAvailability);
        next = values.MoveNextAsync().AsTask();
        catalog.PublishCatalogProbe(Snapshot("available", selected: true));
        Assert.False(next.IsCompleted);
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }}
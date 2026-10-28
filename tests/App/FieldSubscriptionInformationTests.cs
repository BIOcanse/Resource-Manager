using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class FieldSubscriptionInformationTests
{
    [Fact]
    public async Task ExactDeliveryIgnoresTimestampsButPushesValuesAndUnavailableTransitions()
    {
        var reference = new FieldReference("volume", "volume_all_free_bytes_actual");
        var field = new DeviceInformationField(DeviceInformationFieldRegistry.VolumeAllFreeBytesActual,
            JsonSerializer.SerializeToElement("500"));
        var declarations = new DeviceInformationSnapshot(DateTimeOffset.UnixEpoch,
            [new("volume", "volume", [field]), new("other", "volume", [field])]);
        var reads = new InformationReadWriteBackend(() => declarations,
            _ => throw new InvalidOperationException("Validation must not collect."), static _ => new(true, null, []));
        var initial = InformationFieldProjection.Read("volume", field, DateTimeOffset.UnixEpoch);
        var unavailable = initial with { Status = FieldObservationStatus.Unavailable, Value = null, ObservedAt = null };
        var delivery = new Batches([initial, initial with { ObservedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) },
            initial with { Value = new(Integer: new MetricInteger(450)) },
            initial with { Value = new(Integer: MetricInteger.FromUnsigned128((UInt128)ulong.MaxValue + 42)) }, unavailable, unavailable]);
        var core = new FieldSubscriptionCore(new ReadWriteCore(reads, new NoDependencies()), delivery);
        var frames = new List<DeviceInformationSnapshot>();
        await foreach (var frame in core.SubscribeInformationChangesAsync("holder", [reference], TimeSpan.FromSeconds(1), declarations))
            frames.Add(frame);
        Assert.Equal(4, frames.Count);
        Assert.Equal("500", Assert.Single(Assert.Single(frames[0].Devices).Fields).Value!.Value.GetString());
        Assert.Equal("450", Assert.Single(Assert.Single(frames[1].Devices).Fields).Value!.Value.GetString());
        Assert.Equal("18446744073709551657", Assert.Single(Assert.Single(frames[2].Devices).Fields).Value!.Value.GetString());
        Assert.Null(Assert.Single(Assert.Single(frames[3].Devices).Fields).Value);
        Assert.Equal(DateTimeOffset.UnixEpoch, frames[0].ObservedAt);
        Assert.All(frames, frame => Assert.Equal("volume", Assert.Single(frame.Devices).DeviceIdentity));
        Assert.Equal(1, delivery.Releases);
        Assert.Throws<ArgumentException>(() => core.SubscribeInformationChangesAsync("bad", [], TimeSpan.FromSeconds(1), declarations));
        Assert.Throws<ArgumentException>(() => core.SubscribeInformationChangesAsync("bad", [reference, reference], TimeSpan.FromSeconds(1), declarations));
    }
    private sealed class Batches(FieldReading[] values) : IFieldSubscriptionBackend
    {
        public int Releases;
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval) => throw new NotSupportedException();
        public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id, ImmutableArray<FieldReference> fields,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            Assert.Equal("volume", Assert.Single(fields).DeviceIdentity);
            try { foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return [value]; await Task.CompletedTask; } }
            finally { Releases++; }
        }
    }
    private sealed class NoDependencies : IReadWriteDependencies {
        public DependencyStatus Query(string id) => new(id, DependencyAvailability.Missing, "No dependency is used.");
        public ValueTask<DependencyPackage> FetchAsync(string id, Uri uri, string destination, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package, CancellationToken token) => throw new NotSupportedException();
    }
}

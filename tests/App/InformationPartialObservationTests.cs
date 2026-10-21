using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using Xunit;
namespace Resource_Manager_APP.Tests;
public sealed class InformationPartialObservationTests
{
    private static readonly DeviceInformationDefinition Definition = new("recorded_text", "recorded", "all", "text", "text", "recorded", "recorded");
    private static DeviceInformationField Value(string value, DateTimeOffset? observedAt = null)
        => new(Definition, JsonSerializer.SerializeToElement(value)) { ObservedAt = observedAt };
    [Fact] public void UnchangedDefaultFieldInheritsSnapshotTime()
    {
        var time = DateTimeOffset.UnixEpoch;
        var field = Value("default");
        var reading = InformationFieldProjection.Read("recorded", field, time);
        Assert.Equal(FieldObservationStatus.Current, reading.Status); Assert.Equal(time, reading.ObservedAt);
    }
    [Fact] public void OldOverrideRetainsObservationWhenSnapshotTimeIsNewerOrNull()
    {
        var time = DateTimeOffset.UnixEpoch;
        var field = Value("retained", time);
        foreach (var snapshotTime in new DateTimeOffset?[] { time.AddSeconds(1), null })
        {
            var snapshot = new DeviceInformationSnapshot(snapshotTime, [new("recorded", "recorded", [field])]);
            var backend = new InformationReadWriteBackend(() => snapshot, _ => ValueTask.FromResult(snapshot), _ => new(true, null, []));
            var reading = backend.ReadCached(new("recorded", Definition.Name));
            Assert.Equal(FieldObservationStatus.Current, reading.Status); Assert.Equal(time, reading.ObservedAt);
            Assert.Equal("retained", reading.Value!.Text);
        }
    }
    [Fact] public void NullValueRemainsUnavailableDespiteAnObservationOverride()
    {
        var field = new DeviceInformationField(Definition, null) { ObservedAt = DateTimeOffset.UnixEpoch };
        var snapshot = new DeviceInformationSnapshot(DateTimeOffset.UtcNow, [new("recorded", "recorded", [field])]);
        var backend = new InformationReadWriteBackend(() => snapshot, _ => ValueTask.FromResult(snapshot), _ => new(true, null, []));
        var reading = backend.ReadCached(new("recorded", Definition.Name));
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status); Assert.Null(reading.Value); Assert.Null(reading.ObservedAt);
    }
}

using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotObservationProjectionTests
{
    [Fact]
    public void CurrentValueMustSatisfyTheExactCatalogRange()
    {
        var observation = CreateObservation(
            NativeMetricSnapshotValueKind.Float64,
            NativeMetricSnapshotMetricFlags.Percentage
                | NativeMetricSnapshotMetricFlags.Nonnegative,
            DoubleBits(0),
            DoubleBits(100),
            101);

        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Unavailable,
            observation.Status);
        Assert.Equal(
            (ulong)NativeMetricSnapshotObservationValidity.Required,
            observation.ValidMask);
        Assert.Equal(0UL, observation.ValueBits);
    }

    [Fact]
    public void ValidCurrentValuePreservesItsExactBits()
    {
        var observation = CreateObservation(
            NativeMetricSnapshotValueKind.Float64,
            NativeMetricSnapshotMetricFlags.Percentage
                | NativeMetricSnapshotMetricFlags.Nonnegative,
            DoubleBits(0),
            DoubleBits(100),
            73.25);

        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Current,
            observation.Status);
        Assert.Equal(
            (ulong)(
                NativeMetricSnapshotObservationValidity.Required
                | NativeMetricSnapshotObservationValidity.Value),
            observation.ValidMask);
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(73.25),
            observation.ValueBits);
    }

    [Fact]
    public void IntegerEncodingOverflowIsUnavailable()
    {
        var observation = CreateObservation(
            NativeMetricSnapshotValueKind.Signed64,
            NativeMetricSnapshotMetricFlags.None,
            unchecked((ulong)long.MinValue),
            unchecked((ulong)long.MaxValue),
            double.MaxValue);

        Assert.Equal(
            (uint)NativeMetricSnapshotObservationStatus.Unavailable,
            observation.Status);
        Assert.Equal(0UL, observation.ValueBits);
    }

    private static NativeMetricSnapshotObservationInput CreateObservation(
        NativeMetricSnapshotValueKind valueKind,
        NativeMetricSnapshotMetricFlags metricFlags,
        ulong minimumValueBits,
        ulong maximumValueBits,
        double value)
    {
        const ulong ruleHandle = 11;
        const ulong metricHandle = 12;
        const ulong sourceHandle = 13;
        const ulong scopeHandle = 14;
        var metric = new NativeMetricSnapshotMetricPlanOutput
        {
            RuleHandle = ruleHandle,
            MetricHandle = metricHandle,
            SourceHandle = sourceHandle,
            ScopeHandle = scopeHandle,
            ValueKind = (uint)valueKind
        };
        var binding = new NativeMetricSnapshotRuleCatalogBinding(
            ruleHandle,
            metricHandle,
            sourceHandle,
            scopeHandle,
            "test.metric",
            "test.source",
            (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
            (uint)valueKind,
            1,
            (uint)metricFlags,
            minimumValueBits,
            maximumValueBits);
        return NativeMetricSnapshotObservationProjection.CreateObservation(
            metric,
            binding,
            new NativeMetricSnapshotSourceObservationIdentity(
                sourceHandle,
                1,
                1),
            DateTimeOffset.UtcNow,
            value);
    }

    private static ulong DoubleBits(double value)
        => BitConverter.DoubleToUInt64Bits(value);
}

using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotObservationProjectionTests
{
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-9007199254740993L)]
    public void CommittedSignedAbiValuesPreserveRawIntegerPrecision(long value)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"signed-frame-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(0, "NVIDIA fixture", "gpu-fixture", 0xA1, 1, 1, true)], [], [], workspace.CreateCatalogHandleMap());
        var binding = catalog.MetricBindings.Values.Single(item => item.FieldCode == ResourceManager.App.Domain.Metrics.MetricFieldCode.NvmlClocksEvents);
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,
            1, NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection([binding.MetricHandle], modes, false, now);
        var source = Assert.Single(plan.Sources);
        var metric = plan.Metrics.Single(item => item.MetricHandle == binding.MetricHandle);
        var observation = NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(metric, binding,
            new(source.SourceHandle, 1, 1), now, 1);
        workspace.CompleteSource(plan, new(source.SourceHandle, 1, 1, now,
            NativeMetricSnapshotSourceStatus.Complete, [observation], null, [], 0, 0), now);
        var frame = workspace.ReadCommitted();
        // A committed ABI frame can carry signed values. Keep the original
        // identity/status and exercise the production projection with exact raw bits.
        var output = frame.Metrics.Single(item => item.MetricHandle == binding.MetricHandle);
        output.ValueKind = (uint)NativeMetricSnapshotValueKind.Signed64;
        output.ValueBits = unchecked((ulong)value);
        var signed = binding with { ValueKind = output.ValueKind, Field = binding.Field! with { ValueKind = "signed64", Unit = "count" } };
        var signedFrame = frame with
        {
            Metrics = [.. frame.Metrics.Select(item => item.MetricHandle == binding.MetricHandle ? output : item)],
            Catalog = frame.Catalog with
            {
                RuleBindings = frame.Catalog.RuleBindings.SetItem(binding.RuleHandle, signed),
                MetricBindings = frame.Catalog.MetricBindings.SetItem(binding.MetricHandle, signed)
            }
        };
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(signedFrame, "CPU", "RAM", 1);
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.Items[binding.MetricId].IntegerValue);
    }

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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMetricSnapshotAbiTests
{
    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(0x0003_0000U, NativeMetricSnapshotAbi.Version);
        Assert.Equal(0x0002_0000U, NativeMetricSnapshotAbi.PersistenceContractVersion);
        Assert.Equal(0x51B7_8849_C815_027DUL, NativeMetricSnapshotAbi.LayoutFingerprint);
        Assert.Equal(
            NativeMetricSnapshotAbi.WireContractFingerprint,
            ComputeWireContractFingerprint());

        AssertSize<NativeMetricSnapshotConfiguration>(160);
        AssertSize<NativeMetricSnapshotCapacity>(104);
        AssertSize<NativeMetricSnapshotSourcePolicyInput>(64);
        AssertSize<NativeMetricSnapshotMetricDefinitionInput>(96);
        AssertSize<NativeMetricSnapshotCatalogReplaceInput>(96);
        AssertSize<NativeMetricSnapshotCompletionHeader>(144);
        AssertSize<NativeMetricSnapshotPlanInput>(88);
        AssertSize<NativeMetricSnapshotPlanOutput>(88);
        AssertSize<NativeMetricSnapshotPlanMetricInput>(32);
        AssertSize<NativeMetricSnapshotSourceModeInput>(48);
        AssertSize<NativeMetricSnapshotSourcePlanOutput>(72);
        AssertSize<NativeMetricSnapshotMetricPlanOutput>(72);
        AssertSize<NativeMetricSnapshotRequestedMetricInput>(32);
        AssertSize<NativeMetricSnapshotObservationInput>(104);
        AssertSize<NativeMetricSnapshotCpuCounterInput>(136);
        AssertSize<NativeMetricSnapshotGpuInventoryInput>(112);
        AssertSize<NativeMetricSnapshotFinalizeInput>(80);
        AssertSize<NativeMetricSnapshotControlInput>(56);
        AssertSize<NativeMetricSnapshotReadInput>(72);
        AssertSize<NativeMetricSnapshotSnapshotHeader>(152);
        AssertSize<NativeMetricSnapshotMetricOutput>(112);
        AssertSize<NativeMetricSnapshotSourceOutput>(128);
        AssertSize<NativeMetricSnapshotGpuInventoryOutput>(120);
        AssertSize<NativeMetricSnapshotRuleStateOutput>(120);
        AssertSize<NativeMetricSnapshotSourcePersistenceOutput>(184);
        AssertSize<NativeMetricSnapshotPersistenceInput>(64);
        AssertSize<NativeMetricSnapshotPersistenceHeader>(144);

        AssertOffset<NativeMetricSnapshotConfiguration>(
            nameof(NativeMetricSnapshotConfiguration.Generation),
            8);
        AssertOffset<NativeMetricSnapshotConfiguration>(
            nameof(NativeMetricSnapshotConfiguration.MaximumPersistenceSourceCount),
            40);
        AssertOffset<NativeMetricSnapshotConfiguration>(
            nameof(NativeMetricSnapshotConfiguration.MaximumFutureSkewMilliseconds),
            72);
        AssertOffset<NativeMetricSnapshotConfiguration>(
            nameof(NativeMetricSnapshotConfiguration.ResidentByteBudget),
            80);
        AssertOffset<NativeMetricSnapshotConfiguration>(
            nameof(NativeMetricSnapshotConfiguration.MaximumPlanMetricCount),
            112);
        AssertOffset<NativeMetricSnapshotConfiguration>(
            nameof(NativeMetricSnapshotConfiguration.GpuLuidIndexCapacity),
            128);
        AssertOffset<NativeMetricSnapshotCompletionHeader>(
            nameof(NativeMetricSnapshotCompletionHeader.CapabilityGeneration),
            64);
        AssertOffset<NativeMetricSnapshotCompletionHeader>(
            nameof(NativeMetricSnapshotCompletionHeader.PlanTokenFingerprint),
            72);
        AssertOffset<NativeMetricSnapshotCompletionHeader>(
            nameof(NativeMetricSnapshotCompletionHeader.RequestedRuleCount),
            96);
        AssertOffset<NativeMetricSnapshotCompletionHeader>(
            nameof(NativeMetricSnapshotCompletionHeader.Status),
            120);
        AssertOffset<NativeMetricSnapshotSnapshotHeader>(
            nameof(NativeMetricSnapshotSnapshotHeader.CommittedGeneration),
            40);
        AssertOffset<NativeMetricSnapshotSnapshotHeader>(
            nameof(NativeMetricSnapshotSnapshotHeader.SourceCount),
            80);
        AssertOffset<NativeMetricSnapshotSnapshotHeader>(
            nameof(NativeMetricSnapshotSnapshotHeader.Flags),
            116);
        AssertOffset<NativeMetricSnapshotSnapshotHeader>(
            nameof(NativeMetricSnapshotSnapshotHeader.SemanticFingerprint),
            128);
        AssertOffset<NativeMetricSnapshotPersistenceHeader>(
            nameof(NativeMetricSnapshotPersistenceHeader.CommittedGeneration),
            32);
        AssertOffset<NativeMetricSnapshotPersistenceHeader>(
            nameof(NativeMetricSnapshotPersistenceHeader.CatalogFingerprint),
            72);
        AssertOffset<NativeMetricSnapshotPersistenceHeader>(
            nameof(NativeMetricSnapshotPersistenceHeader.RuleStateCount),
            80);
        AssertOffset<NativeMetricSnapshotPersistenceHeader>(
            nameof(NativeMetricSnapshotPersistenceHeader.Checksum),
            104);
        AssertOffset<NativeMetricSnapshotRuleStateOutput>(
            nameof(NativeMetricSnapshotRuleStateOutput.CapabilityGeneration),
            104);
        AssertOffset<NativeMetricSnapshotRuleStateOutput>(
            nameof(
                NativeMetricSnapshotRuleStateOutput
                    .UnsupportedUntilCapabilityGeneration),
            112);
        AssertOffset<NativeMetricSnapshotSourcePersistenceOutput>(
            nameof(NativeMetricSnapshotSourcePersistenceOutput.GpuInventoryCount),
            168);
        AssertOffset<NativeMetricSnapshotSourcePersistenceOutput>(
            nameof(NativeMetricSnapshotSourcePersistenceOutput.GpuRetainedCount),
            172);
    }

    [Fact]
    public void PublishedEnumsAndMasksMatchTheZigContract()
    {
        Assert.Equal(5U, (uint)NativeMetricSnapshotPhase.FailedRetained);
        Assert.Equal(2U, (uint)NativeMetricSnapshotSourceRole.GpuInventory);
        Assert.Equal(5U, (uint)NativeMetricSnapshotSourceStatus.Skipped);
        Assert.Equal(3U, (uint)NativeMetricSnapshotZoneMode.Freeze);
        Assert.Equal(4U, (uint)NativeMetricSnapshotObservationStatus.Skipped);
        Assert.Equal(5U, (uint)NativeMetricSnapshotMetricStatus.Skipped);
        Assert.Equal(13U, (uint)NativeMetricSnapshotMetricKind.CustomNumeric);
        Assert.Equal(4U, (uint)NativeMetricSnapshotScopeKind.GpuAdapter);
        Assert.Equal(3U, (uint)NativeMetricSnapshotValueKind.Unsigned64);
        Assert.Equal(0x0FU, (uint)NativeMetricSnapshotMetricFlags.Known);
        Assert.Equal(0x1FU, (uint)NativeMetricSnapshotSourceResetReason.Known);
        Assert.Equal(0x1FU, (uint)NativeMetricSnapshotSnapshotFlags.Known);
        Assert.Equal(0x07UL, (ulong)NativeMetricSnapshotObservationValidity.Known);
        Assert.Equal(0x0FUL, (ulong)NativeMetricSnapshotCpuCounterValidity.Required);
        Assert.Equal(0x03U, (uint)NativeMetricSnapshotMetricPlanFlags.Known);
        Assert.Equal(0x03U, (uint)NativeMetricSnapshotSourcePlanFlags.Known);
        Assert.Equal(0x0FUL, (ulong)NativeMetricSnapshotInventoryValidity.Required);
    }

    [Fact]
    public void RealNativeLibraryPublishesExactWireContractAndCapacity()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeMetricSnapshotSession(in configuration);

        Assert.Equal(
            NativeMetricSnapshotAbi.Version,
            NativeMetricSnapshotSession.GetAbiVersion());
        Assert.Equal(
            NativeMetricSnapshotAbi.WireContractFingerprint,
            NativeMetricSnapshotSession.GetWireContractFingerprint());
        Assert.Equal(
            configuration.MaximumSourceCount,
            session.Capacity.SourceCapacity);
        Assert.Equal(
            configuration.MaximumMetricCount,
            session.Capacity.MetricCapacity);
        Assert.Equal(
            configuration.MaximumGpuAdapterCount,
            session.Capacity.GpuAdapterCapacity);
        Assert.InRange(
            session.Capacity.ResidentByteCount,
            1UL,
            configuration.ResidentByteBudget);
        Assert.Equal(
            NativeMetricSnapshotStatus.Ok,
            session.QueryHeader(out var header));
        Assert.Equal(configuration.Generation, header.ConfigurationGeneration);
        Assert.Equal((uint)NativeMetricSnapshotPhase.Empty, header.Phase);
    }

    [Fact]
    public void NativeCatalogFingerprintIsStableAndSemantic()
    {
        NativeMetricSnapshotSourcePolicyInput[] sources =
        [
            new()
            {
                StructSize = checked(
                    (uint)Unsafe.SizeOf<
                        NativeMetricSnapshotSourcePolicyInput>()),
                SourceHandle = 1,
                SourceRole = (uint)NativeMetricSnapshotSourceRole.Metrics,
                Priority = 100,
                RetentionPolicy = (uint)
                    NativeMetricSnapshotRetentionPolicy.RetainLastGood,
                CapabilityMask = 1,
                SemanticFingerprint = 11
            }
        ];
        NativeMetricSnapshotMetricDefinitionInput[] rules =
        [
            new()
            {
                StructSize = checked(
                    (uint)Unsafe.SizeOf<
                        NativeMetricSnapshotMetricDefinitionInput>()),
                RuleHandle = 1,
                MetricHandle = 1,
                SourceHandle = 1,
                ScopeHandle = 1,
                CapabilityMask = 1,
                MetricKind = (uint)NativeMetricSnapshotMetricKind.CpuUsage,
                ScopeKind = (uint)NativeMetricSnapshotScopeKind.Cpu,
                ValueKind = (uint)NativeMetricSnapshotValueKind.Float64,
                RetentionPolicy = (uint)
                    NativeMetricSnapshotRetentionPolicy.RetainLastGood,
                SourcePriority = 100,
                MinimumValueBits = (ulong)BitConverter.DoubleToInt64Bits(0),
                MaximumValueBits = (ulong)BitConverter.DoubleToInt64Bits(100),
                SemanticFingerprint = 22
            }
        ];

        var first = NativeMetricSnapshotSession.CalculateCatalogFingerprint(
            sources,
            rules);
        var second = NativeMetricSnapshotSession.CalculateCatalogFingerprint(
            sources,
            rules);
        rules[0].SourcePriority++;
        var changed = NativeMetricSnapshotSession.CalculateCatalogFingerprint(
            sources,
            rules);

        Assert.NotEqual(0UL, first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }

    private static NativeMetricSnapshotConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativeMetricSnapshotAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeMetricSnapshotConfiguration>()),
            Generation = 1,
            MaximumSourceCount = 8,
            MaximumMetricCount = 32,
            MaximumRuleCount = 64,
            MaximumRequestedCount = 32,
            MaximumObservationCount = 64,
            MaximumGpuAdapterCount = 8,
            MaximumPersistenceSourceCount = 8,
            MaximumPersistenceRuleCount = 64,
            MaximumPersistenceGpuCount = 8,
            SourceIndexCapacity = 16,
            MetricIndexCapacity = 64,
            RuleIndexCapacity = 128,
            GpuIndexCapacity = 16,
            MaximumFutureSkewMilliseconds = 30_000,
            ResidentByteBudget = 4 * 1024 * 1024,
            CatalogContractVersion = NativeMetricSnapshotAbi.CatalogContractVersion,
            ValueContractVersion = NativeMetricSnapshotAbi.ValueContractVersion,
            ObservationContractVersion =
                NativeMetricSnapshotAbi.ObservationContractVersion,
            InventoryContractVersion = NativeMetricSnapshotAbi.InventoryContractVersion,
            PersistenceContractVersion =
                NativeMetricSnapshotAbi.PersistenceContractVersion,
            Flags = 0,
            MaximumPlanMetricCount = 32,
            MaximumSourceModeCount = 8,
            MaximumSourcePlanCount = 8,
            MaximumMetricPlanCount = 64,
            GpuLuidIndexCapacity = 16,
            GpuKeyIndexCapacity = 16,
            ReservedU32 = 0
        };

    private static void AssertSize<T>(int expected)
        where T : struct
        => Assert.Equal(expected, Unsafe.SizeOf<T>());

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));

    private static ulong ComputeWireContractFingerprint()
    {
        Type[] types =
        [
            typeof(NativeMetricSnapshotConfiguration),
            typeof(NativeMetricSnapshotCapacity),
            typeof(NativeMetricSnapshotSourcePolicyInput),
            typeof(NativeMetricSnapshotMetricDefinitionInput),
            typeof(NativeMetricSnapshotCatalogReplaceInput),
            typeof(NativeMetricSnapshotCompletionHeader),
            typeof(NativeMetricSnapshotPlanInput),
            typeof(NativeMetricSnapshotPlanOutput),
            typeof(NativeMetricSnapshotPlanMetricInput),
            typeof(NativeMetricSnapshotSourceModeInput),
            typeof(NativeMetricSnapshotSourcePlanOutput),
            typeof(NativeMetricSnapshotMetricPlanOutput),
            typeof(NativeMetricSnapshotRequestedMetricInput),
            typeof(NativeMetricSnapshotObservationInput),
            typeof(NativeMetricSnapshotCpuCounterInput),
            typeof(NativeMetricSnapshotGpuInventoryInput),
            typeof(NativeMetricSnapshotFinalizeInput),
            typeof(NativeMetricSnapshotControlInput),
            typeof(NativeMetricSnapshotReadInput),
            typeof(NativeMetricSnapshotSnapshotHeader),
            typeof(NativeMetricSnapshotMetricOutput),
            typeof(NativeMetricSnapshotSourceOutput),
            typeof(NativeMetricSnapshotGpuInventoryOutput),
            typeof(NativeMetricSnapshotRuleStateOutput),
            typeof(NativeMetricSnapshotSourcePersistenceOutput),
            typeof(NativeMetricSnapshotPersistenceInput),
            typeof(NativeMetricSnapshotPersistenceHeader)
        ];
        ulong hash = 0xCBF2_9CE4_8422_2325;
        hash = Mix(hash, (ulong)types.Length);
        foreach (Type type in types)
        {
            FieldInfo[] fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(field => field.MetadataToken)
                .ToArray();
            hash = Mix(hash, (ulong)Marshal.SizeOf(type));
            hash = Mix(hash, (ulong)fields.Max(FieldAlignment));
            hash = Mix(hash, (ulong)fields.Length);
            foreach (FieldInfo field in fields)
            {
                hash = MixBytes(hash, ToSnakeCase(field.Name));
                hash = MixByte(hash, 0xFF);
                hash = Mix(hash, (ulong)Marshal.OffsetOf(type, field.Name).ToInt64());
                hash = Mix(hash, (ulong)FieldSize(field));
                hash = Mix(hash, (ulong)FieldAlignment(field));
                hash = MixFieldType(hash, field);
            }
        }

        ulong[] semanticValues =
        [
            NativeMetricSnapshotAbi.Version,
            NativeMetricSnapshotAbi.CatalogContractVersion,
            NativeMetricSnapshotAbi.ValueContractVersion,
            NativeMetricSnapshotAbi.ObservationContractVersion,
            NativeMetricSnapshotAbi.InventoryContractVersion,
            NativeMetricSnapshotAbi.PersistenceContractVersion,
            NativeMetricSnapshotAbi.CpuCounterContractVersion,

            (uint)NativeMetricSnapshotPhase.Empty,
            (uint)NativeMetricSnapshotPhase.CatalogReady,
            (uint)NativeMetricSnapshotPhase.CompletionOpen,
            (uint)NativeMetricSnapshotPhase.Ready,
            (uint)NativeMetricSnapshotPhase.FailedRetained,
            (uint)NativeMetricSnapshotSourceRole.Metrics,
            (uint)NativeMetricSnapshotSourceRole.GpuInventory,
            (uint)NativeMetricSnapshotSourceStatus.Complete,
            (uint)NativeMetricSnapshotSourceStatus.Partial,
            (uint)NativeMetricSnapshotSourceStatus.Unavailable,
            (uint)NativeMetricSnapshotSourceStatus.Unsupported,
            (uint)NativeMetricSnapshotSourceStatus.Skipped,
            (uint)NativeMetricSnapshotZoneMode.Normal,
            (uint)NativeMetricSnapshotZoneMode.LowPower,
            (uint)NativeMetricSnapshotZoneMode.Freeze,
            (uint)NativeMetricSnapshotSourceAvailability.Available,
            (uint)NativeMetricSnapshotSourceAvailability.Unavailable,
            (uint)NativeMetricSnapshotSourceAvailability.Unsupported,
            (uint)NativeMetricSnapshotObservationStatus.Current,
            (uint)NativeMetricSnapshotObservationStatus.Unavailable,
            (uint)NativeMetricSnapshotObservationStatus.Unsupported,
            (uint)NativeMetricSnapshotObservationStatus.Skipped,
            (uint)NativeMetricSnapshotMetricStatus.Current,
            (uint)NativeMetricSnapshotMetricStatus.Retained,
            (uint)NativeMetricSnapshotMetricStatus.Unavailable,
            (uint)NativeMetricSnapshotMetricStatus.Unsupported,
            (uint)NativeMetricSnapshotMetricStatus.Skipped,
            (uint)NativeMetricSnapshotInventoryStatus.Current,
            (uint)NativeMetricSnapshotInventoryStatus.Retained,
            (uint)NativeMetricSnapshotInventoryStatus.Unavailable,
            (uint)NativeMetricSnapshotInventoryStatus.Unsupported,
            (uint)NativeMetricSnapshotRetentionPolicy.RetainLastGood,
            (uint)NativeMetricSnapshotRetentionPolicy.MarkUnavailable,
            (uint)NativeMetricSnapshotMetricKind.CpuUsage,
            (uint)NativeMetricSnapshotMetricKind.CpuFrequency,
            (uint)NativeMetricSnapshotMetricKind.CpuSensor,
            (uint)NativeMetricSnapshotMetricKind.MemoryUsed,
            (uint)NativeMetricSnapshotMetricKind.MemoryTotal,
            (uint)NativeMetricSnapshotMetricKind.VirtualMemoryUsed,
            (uint)NativeMetricSnapshotMetricKind.VirtualMemoryTotal,
            (uint)NativeMetricSnapshotMetricKind.GpuUsage,
            (uint)NativeMetricSnapshotMetricKind.GpuClock,
            (uint)NativeMetricSnapshotMetricKind.GpuVramUsed,
            (uint)NativeMetricSnapshotMetricKind.GpuVramTotal,
            (uint)NativeMetricSnapshotMetricKind.GpuSensor,
            (uint)NativeMetricSnapshotMetricKind.CustomNumeric,
            (uint)NativeMetricSnapshotScopeKind.Host,
            (uint)NativeMetricSnapshotScopeKind.Cpu,
            (uint)NativeMetricSnapshotScopeKind.Memory,
            (uint)NativeMetricSnapshotScopeKind.GpuAdapter,
            (uint)NativeMetricSnapshotValueKind.Float64,
            (uint)NativeMetricSnapshotValueKind.Signed64,
            (uint)NativeMetricSnapshotValueKind.Unsigned64,

            (uint)NativeMetricSnapshotMetricFlags.Percentage,
            (uint)NativeMetricSnapshotMetricFlags.Nonnegative,
            (uint)NativeMetricSnapshotMetricFlags.UsedValue,
            (uint)NativeMetricSnapshotMetricFlags.TotalValue,
            (uint)NativeMetricSnapshotMetricFlags.Known,
            (uint)NativeMetricSnapshotSourceFlags.Required,
            (uint)NativeMetricSnapshotSourceFlags.Known,
            (uint)NativeMetricSnapshotSourceResetReason.IncarnationChanged,
            (uint)NativeMetricSnapshotSourceResetReason.CounterRegressed,
            (uint)NativeMetricSnapshotSourceResetReason.MonotonicRegressed,
            (uint)NativeMetricSnapshotSourceResetReason.TickFrequencyChanged,
            (uint)NativeMetricSnapshotSourceResetReason.ArithmeticOverflow,
            (uint)NativeMetricSnapshotSourceResetReason.Known,
            (uint)NativeMetricSnapshotSnapshotFlags.CatalogLoaded,
            (uint)NativeMetricSnapshotSnapshotFlags.CommittedData,
            (uint)NativeMetricSnapshotSnapshotFlags.RetainedData,
            (uint)NativeMetricSnapshotSnapshotFlags.GpuInventoryPresent,
            (uint)NativeMetricSnapshotSnapshotFlags.CompletionOpen,
            (uint)NativeMetricSnapshotSnapshotFlags.Known,
            (ulong)NativeMetricSnapshotPlanFlags.IncludeGpuInventory,
            (ulong)NativeMetricSnapshotPlanFlags.Known,
            (uint)NativeMetricSnapshotPlanOutputFlags.GpuInventorySelected,
            (uint)NativeMetricSnapshotPlanOutputFlags.GpuInventoryUnavailable,
            (uint)NativeMetricSnapshotPlanOutputFlags.Known,
            (ulong)NativeMetricSnapshotObservationValidity.Value,
            (ulong)NativeMetricSnapshotObservationValidity.ObservedAt,
            (ulong)NativeMetricSnapshotObservationValidity.Capability,
            (ulong)NativeMetricSnapshotObservationValidity.Required,
            (ulong)NativeMetricSnapshotObservationValidity.Known,
            (ulong)NativeMetricSnapshotCpuCounterValidity.Counters,
            (ulong)NativeMetricSnapshotCpuCounterValidity.MonotonicTime,
            (ulong)NativeMetricSnapshotCpuCounterValidity.ObservedAt,
            (ulong)NativeMetricSnapshotCpuCounterValidity.Capability,
            (ulong)NativeMetricSnapshotCpuCounterValidity.Required,
            (ulong)NativeMetricSnapshotCpuCounterValidity.Known,
            (uint)NativeMetricSnapshotMetricPlanFlags.Selected,
            (uint)NativeMetricSnapshotMetricPlanFlags.Unavailable,
            (uint)NativeMetricSnapshotMetricPlanFlags.Known,
            (uint)NativeMetricSnapshotSourcePlanFlags.Selected,
            (uint)NativeMetricSnapshotSourcePlanFlags.LowPower,
            (uint)NativeMetricSnapshotSourcePlanFlags.Known,
            (ulong)NativeMetricSnapshotInventoryValidity.AdapterIdentity,
            (ulong)NativeMetricSnapshotInventoryValidity.ObservedAt,
            (ulong)NativeMetricSnapshotInventoryValidity.Capability,
            (ulong)NativeMetricSnapshotInventoryValidity.Topology,
            (ulong)NativeMetricSnapshotInventoryValidity.Required,
            (ulong)NativeMetricSnapshotInventoryValidity.Known
        ];
        hash = Mix(hash, (ulong)semanticValues.Length);
        foreach (ulong value in semanticValues)
        {
            hash = Mix(hash, value);
        }

        return hash;
    }

    private static ulong MixFieldType(ulong hash, FieldInfo field)
    {
        FixedBufferAttribute? fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
        if (fixedBuffer is not null)
        {
            hash = Mix(hash, 2);
            hash = Mix(hash, (ulong)fixedBuffer.Length);
            return MixIntegerType(hash, fixedBuffer.ElementType);
        }

        return MixIntegerType(hash, field.FieldType);
    }

    private static ulong MixIntegerType(ulong hash, Type type)
    {
        hash = Mix(hash, 1);
        hash = Mix(hash, type == typeof(uint) || type == typeof(ulong) ? 1UL : 0UL);
        return Mix(hash, (ulong)(PrimitiveSize(type) * 8));
    }

    private static int FieldSize(FieldInfo field)
    {
        FixedBufferAttribute? fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
        return fixedBuffer is null
            ? PrimitiveSize(field.FieldType)
            : checked(PrimitiveSize(fixedBuffer.ElementType) * fixedBuffer.Length);
    }

    private static int FieldAlignment(FieldInfo field)
    {
        FixedBufferAttribute? fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
        return PrimitiveSize(fixedBuffer?.ElementType ?? field.FieldType);
    }

    private static int PrimitiveSize(Type type)
        => type == typeof(uint) || type == typeof(int)
            ? 4
            : type == typeof(ulong) || type == typeof(long)
                ? 8
                : throw new InvalidOperationException($"Unsupported ABI field type {type}.");

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int index = 0; index < 8; index++)
        {
            hash = MixByte(hash, (byte)value);
            value >>= 8;
        }

        return hash;
    }

    private static ulong MixBytes(ulong hash, string value)
    {
        foreach (byte character in System.Text.Encoding.ASCII.GetBytes(value))
        {
            hash = MixByte(hash, character);
        }
        return hash;
    }

    private static ulong MixByte(ulong hash, byte value)
        => (hash ^ value) * 0x100_0000_01B3UL;

    private static string ToSnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }
}

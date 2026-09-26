using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static unsafe class NativeSmartCoordinatorConfigurationWriter
{
    public static NativeSmartCoordinatorConfiguration Create(
        CompiledHostManagerSmartCoordinatorBuildPlan build,
        CompiledHostManagerSmartCoordinatorRecreatePlan recreate,
        CompiledHostManagerSmartCoordinatorHotPublishPlan hotPublish,
        ulong generation)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(recreate);
        ArgumentNullException.ThrowIfNull(hotPublish);
        if (!build.IsPublished || build.AbiVersion != NativeSmartCoordinatorAbi.Version)
        {
            throw new InvalidDataException("The smart coordinator build identity is not compatible with the native ABI.");
        }
        if (!recreate.IsPublished || !hotPublish.IsPublished || generation == 0)
        {
            throw new InvalidDataException("The smart coordinator configuration is incomplete.");
        }
        if (!hotPublish.CpuAdapterPolicy.StateMultipliers.AsSpan()
            .SequenceEqual(hotPublish.ProcessStateMultipliers.AsSpan()))
        {
            throw new InvalidDataException(
                "The CPU adapter ABI state multipliers must mirror the process policy multipliers.");
        }

        var configuration = new NativeSmartCoordinatorConfiguration
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorConfiguration>(),
            Generation = generation,
            FieldMask = NativeSmartCoordinatorConfigFields.Required,
            FeatureFlags = (NativeSmartCoordinatorFeatures)hotPublish.FeatureFlags,
            MaximumProcesses = checked((uint)recreate.MaximumProcesses),
            MaximumSoftwareGroups = checked((uint)recreate.MaximumSoftwareGroups),
            MaximumGpuStates = checked((uint)recreate.MaximumGpuStates),
            MaximumInputRows = checked((uint)recreate.MaximumInputRows),
            MaximumActions = checked((uint)recreate.MaximumActions),
            MaximumReservations = checked((uint)recreate.MaximumReservations),
            MaximumAtomicGroups = checked((uint)recreate.MaximumAtomicGroups),
            NormalIntervalMilliseconds = checked((uint)hotPublish.NormalIntervalMilliseconds),
            EventIntervalMilliseconds = checked((uint)hotPublish.EventIntervalMilliseconds),
            EventBoostMilliseconds = checked((uint)hotPublish.EventBoostMilliseconds),
            GameStartGraceMilliseconds = checked((uint)hotPublish.GameStartGraceMilliseconds),
            RequiredConsecutiveDecisions = checked((uint)hotPublish.RequiredConsecutiveDecisions),
            FailureRetryMilliseconds = checked((uint)hotPublish.FailureRetryMilliseconds),
            ReservationTimeoutMilliseconds = checked((uint)hotPublish.ReservationTimeoutMilliseconds),
            A1MinimumCpuScore = hotPublish.A1MinimumCpuScore,
            DefaultMinimumCpuScoreScale = hotPublish.DefaultMinimumCpuScoreScale,
            Level1MaximumCpuScoreScale = hotPublish.Level1MaximumCpuScoreScale,
            Level2MaximumCpuScoreScale = hotPublish.Level2MaximumCpuScoreScale,
            Level3MaximumCpuScoreScale = hotPublish.Level3MaximumCpuScoreScale,
            LowTierLevel4MaximumCpuScoreScale = hotPublish.LowTierLevel4MaximumCpuScoreScale,
            ReservedProcessPolicy0 = 0,
            HighTierMinimumBaseScore = hotPublish.BaseScoreTiers.HighMinimumBaseScore,
            MiddleTierMinimumBaseScore = hotPublish.BaseScoreTiers.MiddleMinimumBaseScore,
            CpuAdapter = CreateAdapterPolicy(hotPublish.CpuAdapterPolicy),
            GpuAdapter = CreateAdapterPolicy(hotPublish.GpuAdapterPolicy)
        };
        SetProcessStateMultipliers(ref configuration, hotPublish.ProcessStateMultipliers.AsSpan());
        return configuration;
    }

    public static string ComputeSha256(
        in NativeSmartCoordinatorConfiguration configuration,
        uint maximumActionsPerRealtimeTick)
    {
        if (maximumActionsPerRealtimeTick == 0)
        {
            throw new InvalidDataException(
                "The smart coordinator runtime-only inputs must be explicit.");
        }
        var value = configuration;
        var values = MemoryMarshal.CreateReadOnlySpan(ref value, 1);
        Span<byte> runtimeInput = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(runtimeInput, maximumActionsPerRealtimeTick);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(values));
        hash.AppendData(runtimeInput);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void SetProcessStateMultipliers(
        ref NativeSmartCoordinatorConfiguration configuration,
        ReadOnlySpan<double> values)
    {
        RequireCompleteStateVector(values);
        fixed (NativeSmartCoordinatorConfiguration* target = &configuration)
        {
            for (var index = 0; index < values.Length; index++)
            {
                target->ProcessStateMultipliers[index] = values[index];
            }
        }
    }

    public static void SetAdapterStateMultipliers(
        ref NativeSmartCoordinatorAdapterPolicyConfiguration configuration,
        ReadOnlySpan<double> values)
    {
        RequireCompleteStateVector(values);
        fixed (NativeSmartCoordinatorAdapterPolicyConfiguration* target = &configuration)
        {
            for (var index = 0; index < values.Length; index++)
            {
                target->StateMultipliers[index] = values[index];
            }
        }
    }

    private static void RequireCompleteStateVector(ReadOnlySpan<double> values)
    {
        if (values.Length != NativeSmartCoordinatorAbi.RuntimeStateCount)
        {
            throw new ArgumentException(
                $"Exactly {NativeSmartCoordinatorAbi.RuntimeStateCount} state multipliers are required.",
                nameof(values));
        }
    }

    private static NativeSmartCoordinatorAdapterPolicyConfiguration CreateAdapterPolicy(
        CompiledHostManagerSmartCoordinatorAdapterPolicyPlan source)
    {
        var result = new NativeSmartCoordinatorAdapterPolicyConfiguration
        {
            ExtremeMinimumScore = source.ExtremeMinimumScore,
            NormalMinimumScore = source.NormalMinimumScore,
            OptimizeMinimumScore = source.OptimizeMinimumScore
        };
        SetAdapterStateMultipliers(ref result, source.StateMultipliers.AsSpan());
        return result;
    }
}

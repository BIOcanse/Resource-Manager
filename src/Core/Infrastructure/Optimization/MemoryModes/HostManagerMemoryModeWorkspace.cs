using System.Collections.Immutable;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed class HostManagerMemoryModeWorkspace : IDisposable
{
    private readonly NativeMemoryModeControllerSession session;
    private readonly NativeMemoryModeConfiguration configuration;
    private readonly NativeMemoryModeSoftwareInput[] softwareInputs;
    private readonly NativeMemoryModeDesiredSoftwareOutput[] outputs;
    private readonly bool[] rankSeen;
    private bool disposed;

    internal HostManagerMemoryModeWorkspace(
        in NativeMemoryModeConfiguration configuration)
    {
        this.configuration = configuration;
        session = new NativeMemoryModeControllerSession(in configuration);
        var capacity = session.Capacity;
        softwareInputs = new NativeMemoryModeSoftwareInput[checked((int)capacity.SoftwareCapacity)];
        outputs = new NativeMemoryModeDesiredSoftwareOutput[checked((int)capacity.OutputCapacity)];
        rankSeen = new bool[Math.Max(1, checked((int)capacity.SoftwareCapacity))];
    }

    internal ulong ConfigurationGeneration => session.Capacity.ConfigurationGeneration;

    internal bool MatchesConfiguration(in NativeMemoryModeConfiguration candidate)
        => configuration.Generation == candidate.Generation
            && configuration.MaximumSoftwareCount == candidate.MaximumSoftwareCount
            && configuration.MaximumOutputCount == candidate.MaximumOutputCount
            && configuration.RatioUnitsMaximum == candidate.RatioUnitsMaximum
            && configuration.UnrestrictedMinimumFreeRatioUnits
                == candidate.UnrestrictedMinimumFreeRatioUnits
            && configuration.NormalMinimumFreeRatioUnits
                == candidate.NormalMinimumFreeRatioUnits
            && configuration.StrongBeginFreeRatioUnits
                == candidate.StrongBeginFreeRatioUnits
            && configuration.MiddleTierMinimumBaseScore
                == candidate.MiddleTierMinimumBaseScore
            && configuration.HighTierMinimumBaseScore
                == candidate.HighTierMinimumBaseScore;

    internal HostManagerMemoryModeDesiredSnapshot Plan(
        HostManagerComputeScoringCycleResult scoring,
        SchedulingProcessFactSnapshot processFacts,
        HostManagerMemorySourceStamp memorySource,
        uint memoryFreeRatioUnits,
        ulong snapshotGeneration,
        bool allowUnrestricted)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scoring);
        ArgumentNullException.ThrowIfNull(processFacts);
        if (!memorySource.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memorySource),
                "Memory mode planning requires a complete source stamp.");
        }
        ArgumentOutOfRangeException.ThrowIfZero(snapshotGeneration);
        if (memoryFreeRatioUnits > configuration.RatioUnitsMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryFreeRatioUnits));
        }

        var cpu = scoring.Cpu ?? throw new InvalidDataException(
            "Memory mode planning requires a complete CPU score domain.");
        ValidateGenerationEnvelope(scoring, cpu, processFacts);
        var softwareScores = cpu.Scores
            .Where(static row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu)
            .OrderBy(static row => row.SoftwareKey)
            .ToArray();
        var softwareCount = softwareScores.Length;
        if (softwareCount > softwareInputs.Length || softwareCount > outputs.Length)
        {
            throw new InvalidDataException("Memory mode planning capacity was exceeded.");
        }
        var softwareBaseScores = AggregateSoftwareBaseScores(cpu, processFacts);
        FillSoftware(scoring.SchedulingGeneration, softwareScores, softwareBaseScores);

        var flags = NativeMemoryModeEnvelopeFlags.Required;
        if (allowUnrestricted) flags |= NativeMemoryModeEnvelopeFlags.AllowUnrestricted;
        var envelope = new NativeMemoryModeGenerationEnvelope
        {
            AbiVersion = NativeMemoryModeControllerAbi.Version,
            StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeGenerationEnvelope>(),
            SoftwareInputStructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSoftwareInput>(),
            SoftwareOutputStructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeDesiredSoftwareOutput>(),
            ConfigurationGeneration = ConfigurationGeneration,
            SchedulingGeneration = scoring.SchedulingGeneration,
            MemorySourceWorkspaceIdentity = memorySource.WorkspaceIdentity,
            MemorySourceCommittedGeneration = memorySource.CommittedGeneration,
            SnapshotGeneration = snapshotGeneration,
            ValidMask = NativeMemoryModeEnvelopeValidity.Required,
            Flags = flags,
            MemoryFreeRatioUnits = memoryFreeRatioUnits,
            SoftwareCount = checked((uint)softwareCount),
            OutputCapacity = checked((uint)softwareCount)
        };
        var nativeSnapshot = default(NativeMemoryModeSnapshot);
        var status = session.Plan(
            in envelope,
            softwareInputs.AsSpan(0, softwareCount),
            outputs.AsSpan(0, softwareCount),
            ref nativeSnapshot);
        if (status != NativeMemoryModeControllerStatus.Ok)
        {
            throw new InvalidOperationException($"Native memory mode planning failed with {status}.");
        }
        ValidateSnapshot(nativeSnapshot, envelope);
        return ConvertSnapshot(nativeSnapshot, softwareCount);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session.Dispose();
    }

    private static void ValidateGenerationEnvelope(
        HostManagerComputeScoringCycleResult scoring,
        HostManagerComputeScoreDomainSnapshot cpu,
        SchedulingProcessFactSnapshot processFacts)
    {
        if (scoring.SchedulingGeneration == 0 ||
            cpu.SchedulingGeneration != scoring.SchedulingGeneration ||
            cpu.SourceGeneration == 0 ||
            cpu.SourceIdentity.InventoryGeneration != processFacts.Generation ||
            !processFacts.IsInventoryCurrentComplete() ||
            cpu.TopologyGeneration != 0 || cpu.TopologyFingerprint != 0)
        {
            throw new InvalidDataException(
                "Memory mode planning inputs do not form one complete CPU scoring generation.");
        }
    }

    private void FillSoftware(
        ulong schedulingGeneration,
        HostManagerComputeScore[] softwareScores,
        IReadOnlyDictionary<ulong, SoftwareBaseScore> softwareBaseScores)
    {
        if (softwareScores.Length != softwareBaseScores.Count)
        {
            throw new InvalidDataException(
                "The memory ordering and base-score software sets differ.");
        }
        ulong previousKey = 0;
        for (var index = 0; index < softwareScores.Length; index++)
        {
            var score = softwareScores[index];
            if (score.SchedulingGeneration != schedulingGeneration || score.SoftwareKey == 0 ||
                score.SoftwareKey <= previousKey || score.AdapterKey != 0 ||
                !double.IsFinite(score.Score) || score.Score < 0 || score.MemberCount == 0 ||
                !softwareBaseScores.TryGetValue(score.SoftwareKey, out var baseScore) ||
                baseScore.MemberCount != score.MemberCount)
            {
                throw new InvalidDataException(
                    "The memory ordering input is not a canonical software CPU score set.");
            }
            previousKey = score.SoftwareKey;
            softwareInputs[index] = new()
            {
                StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSoftwareInput>(),
                ValidMask = NativeMemoryModeSoftwareValidity.Required,
                SoftwareKey = score.SoftwareKey,
                SchedulingGeneration = schedulingGeneration,
                CpuScore = score.Score,
                BaseScore = baseScore.Value,
                SourceIndex = checked((uint)index)
            };
        }
    }

    private static IReadOnlyDictionary<ulong, SoftwareBaseScore> AggregateSoftwareBaseScores(
        HostManagerComputeScoreDomainSnapshot cpu,
        SchedulingProcessFactSnapshot processFacts)
    {
        var factsByIdentity = processFacts.Processes.ToDictionary(
            static process => new HostManagerComputeProcessIdentity(process.ProcessId, process.ProcessStartKey));
        var members = new HashSet<HostManagerComputeProcessIdentity>();
        var result = new Dictionary<ulong, SoftwareBaseScore>();
        foreach (var score in cpu.Scores)
        {
            if (score.Kind != NativeComputeScoringOutputKind.ProcessCpu)
            {
                continue;
            }
            var identity = new HostManagerComputeProcessIdentity(score.ProcessId, score.ProcessStartKey);
            if (score.SchedulingGeneration != cpu.SchedulingGeneration
                || score.MemberCount != 1
                || !members.Add(identity)
                || !factsByIdentity.TryGetValue(identity, out var process))
            {
                throw new InvalidDataException(
                    "The memory base-score input is not bound to the observed CPU process members.");
            }
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            if (softwareKey == 0 || softwareKey != score.SoftwareKey
                || !double.IsFinite(process.BaseScore) || process.BaseScore < 0)
            {
                throw new InvalidDataException(
                    "The memory base-score input contains an invalid software fact.");
            }

            if (result.TryGetValue(softwareKey, out var current))
            {
                result[softwareKey] = new(
                    Math.Max(current.Value, process.BaseScore),
                    checked(current.MemberCount + 1));
            }
            else
            {
                result.Add(softwareKey, new(process.BaseScore, 1));
            }
        }
        return result;
    }

    private static unsafe void ValidateSnapshot(
        NativeMemoryModeSnapshot snapshot,
        NativeMemoryModeGenerationEnvelope envelope)
    {
        var expectedFlags = NativeMemoryModeSnapshotFlags.FullReplacement |
            (envelope.Flags.HasFlag(NativeMemoryModeEnvelopeFlags.AllowUnrestricted)
                ? NativeMemoryModeSnapshotFlags.UnrestrictedAllowed
                : 0);
        if (snapshot.AbiVersion != NativeMemoryModeControllerAbi.Version ||
            snapshot.StructSize != NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSnapshot>() ||
            snapshot.ConfigurationGeneration != envelope.ConfigurationGeneration ||
            snapshot.SchedulingGeneration != envelope.SchedulingGeneration ||
            snapshot.MemorySourceWorkspaceIdentity
                != envelope.MemorySourceWorkspaceIdentity ||
            snapshot.MemorySourceCommittedGeneration
                != envelope.MemorySourceCommittedGeneration ||
            snapshot.SnapshotGeneration != envelope.SnapshotGeneration ||
            snapshot.Flags != expectedFlags ||
            snapshot.OutputCount != envelope.SoftwareCount || snapshot.Reserved0 != 0 ||
            snapshot.Reserved[0] != 0 || snapshot.Reserved[1] != 0 ||
            snapshot.Reserved[2] != 0)
        {
            throw new InvalidOperationException(
                "Native memory mode snapshot violated its managed contract.");
        }
    }

    private HostManagerMemoryModeDesiredSnapshot ConvertSnapshot(
        NativeMemoryModeSnapshot snapshot,
        int softwareCount)
    {
        Array.Clear(rankSeen, 0, softwareCount);
        var software = ImmutableArray.CreateBuilder<HostManagerDesiredMemoryMode>(softwareCount);
        uint optimizeCount = 0;
        uint strongestCount = 0;
        for (var index = 0; index < softwareCount; index++)
        {
            var row = outputs[index];
            ValidateOutput(row, snapshot, index, softwareCount);
            if (rankSeen[row.Rank])
            {
                throw new InvalidOperationException("Native memory ranks are not unique.");
            }
            rankSeen[row.Rank] = true;
            if (row.Mode == NativeMemoryMode.Optimize) optimizeCount++;
            if (row.Mode == NativeMemoryMode.PagedFrozen) strongestCount++;
            software.Add(new(
                row.SoftwareKey,
                row.SchedulingGeneration,
                row.SnapshotGeneration,
                row.Score,
                row.Rank,
                row.Mode,
                row.BaseScoreAllowedGrades,
                row.BaseScore,
                row.Flags.HasFlag(NativeMemoryModeDesiredSoftwareFlags.BaseScoreClamped)));
        }
        if (optimizeCount != snapshot.OptimizeCount || strongestCount != snapshot.StrongestCount)
        {
            throw new InvalidOperationException(
                "Native memory mode summary counts do not match its rows.");
        }

        return new(
            snapshot.ConfigurationGeneration,
            snapshot.SchedulingGeneration,
            new HostManagerMemorySourceStamp(
                snapshot.MemorySourceWorkspaceIdentity,
                snapshot.MemorySourceCommittedGeneration),
            snapshot.SnapshotGeneration,
            snapshot.Flags.HasFlag(NativeMemoryModeSnapshotFlags.UnrestrictedAllowed),
            snapshot.OptimizeCount,
            snapshot.StrongestCount,
            software.MoveToImmutable());
    }

    private void ValidateOutput(
        NativeMemoryModeDesiredSoftwareOutput row,
        NativeMemoryModeSnapshot snapshot,
        int index,
        int softwareCount)
    {
        var input = softwareInputs[index];
        if (row.StructSize != NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeDesiredSoftwareOutput>() ||
            row.ValidMask != NativeMemoryModeDesiredSoftwareValidity.Required ||
            row.SoftwareKey != input.SoftwareKey || row.SnapshotGeneration != snapshot.SnapshotGeneration ||
            row.SchedulingGeneration != snapshot.SchedulingGeneration || row.Score != input.CpuScore ||
            row.BaseScore != input.BaseScore ||
            row.Rank >= softwareCount || row.SourceIndex != index ||
            row.Mode is < NativeMemoryMode.Unrestricted or > NativeMemoryMode.PagedFrozen ||
            (row.BaseScoreAllowedGrades & NativeMemoryGradeSet.Normal) == 0 ||
            (row.BaseScoreAllowedGrades & ~NativeMemoryGradeSet.Known) != 0 ||
            (row.Flags & ~NativeMemoryModeDesiredSoftwareFlags.Known) != 0 || row.Reserved0 != 0)
        {
            throw new InvalidOperationException(
                "Native memory mode controller emitted an invalid output row.");
        }
    }

    private readonly record struct SoftwareBaseScore(double Value, uint MemberCount);
}

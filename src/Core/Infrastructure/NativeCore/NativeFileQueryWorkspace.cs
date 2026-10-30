using ResourceManager.ReadWrite.Native;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
namespace ResourceManager.App.Infrastructure.NativeCore;

// Product compiled-plan mapping only; one SDK pool owns sessions, epochs and reusable lease release.
internal sealed class NativeFileQueryWorkspace : INativeFileQueryLeaseSource, IDisposable
{
    private readonly NativeFileQueryPool pool;
    // The plan's query_session_count is the pool capacity; the shape (memory/CPU tier options
    // file_query_sessions_max / file_query_sessions_prebuilt / file_query_idle_sessions) says how many
    // sessions live, are created up front and stay idle. Without a shape every session is created up front.
    internal NativeFileQueryWorkspace(CompiledHostManagerFileQueryPlan plan,
        Func<ulong, uint, INativeFileQueryScratch> createScratch, Func<NativeFileQueryPoolShape>? shape = null)
        : this(CreateConfiguration(plan), plan.Recreate.QuerySessionCount, checked((ulong)plan.HotPublish.TotalResidentByteBudget), createScratch, shape) { }
    internal NativeFileQueryWorkspace(in NativeFileQueryConfiguration configuration, int sessionCount, ulong totalResidentByteBudget,
        Func<ulong, uint, INativeFileQueryScratch> createScratch, Func<NativeFileQueryPoolShape>? shape = null)
        => pool = new(in configuration, sessionCount, totalResidentByteBudget, createScratch, shape);
    public ValueTask<NativeFileQueryLease> AcquireAsync(CancellationToken token) => pool.AcquireAsync(token);
    public ulong NextEpoch() => pool.NextEpoch();
    public void Dispose() => pool.Dispose();
    internal static unsafe NativeFileQueryConfiguration CreateConfiguration(
        CompiledHostManagerFileQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("The Host Manager file-query plan is not published.");
        }
        var capacity = plan.Recreate.Capacity;
        var hot = plan.HotPublish;
        return new NativeFileQueryConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked((uint)Unsafe.SizeOf<NativeFileQueryConfiguration>()),
            Generation = plan.ConfigurationGeneration,
            MaximumQueryUtf8ByteCount = checked((uint)capacity.MaximumQueryUtf8ByteCount),
            MaximumQueryRuneCount = checked((uint)capacity.MaximumQueryRuneCount),
            MaximumPlanUtf8ByteCount = checked((uint)capacity.MaximumPlanUtf8ByteCount),
            MaximumSourcePlanCount = checked((uint)capacity.MaximumSourcePlanCount),
            MaximumCandidateCountPerSource = checked((uint)capacity.MaximumCandidateCountPerSource),
            MaximumSubmittedCandidateCount = checked((uint)capacity.MaximumSubmittedCandidateCount),
            MaximumUniqueCandidateCount = checked((uint)capacity.MaximumUniqueCandidateCount),
            MaximumCandidateSubmitBatchCount = checked((uint)capacity.MaximumCandidateSubmitBatchCount),
            MaximumCandidateSubmitUtf8ByteCount = checked((uint)capacity.MaximumCandidateSubmitUtf8ByteCount),
            CandidateTextArenaByteCount = checked((uint)capacity.CandidateTextArenaByteCount),
            MaximumFileNameUtf8ByteCount = checked((uint)capacity.MaximumFileNameUtf8ByteCount),
            MaximumResultCount = checked((uint)capacity.MaximumResultCount),
            EntryIndexCapacity = checked((uint)capacity.EntryIndexCapacity),
            OrdinalIndexCapacity = checked((uint)capacity.OrdinalIndexCapacity),
            ShortQueryRuneThreshold = checked((uint)hot.ShortQueryRuneThreshold),
            UnicodeTokenizerVersion = hot.UnicodeTokenizerVersion,
            UnicodeRemoveDiacriticsMode = hot.UnicodeRemoveDiacriticsMode,
            TrigramTokenizerContractVersion = hot.TrigramTokenizerContractVersion,
            CandidateLimitMultiplier = checked((uint)hot.CandidateLimitMultiplier),
            CandidateLimitFloor = checked((uint)hot.CandidateLimitFloor),
            CandidateLimitCeiling = checked((uint)hot.CandidateLimitCeiling),
            FileNamePriority = checked((uint)hot.FileNamePriority),
            RelativePathPriority = checked((uint)hot.RelativePathPriority),
            SoftwareNamePriority = checked((uint)hot.SoftwareNamePriority),
            Flags = 0,
            TextMatchingVersion = hot.TextMatchingVersion,
            ResidentByteBudget = checked((ulong)hot.PerSessionResidentByteBudget)
        };
    }

}

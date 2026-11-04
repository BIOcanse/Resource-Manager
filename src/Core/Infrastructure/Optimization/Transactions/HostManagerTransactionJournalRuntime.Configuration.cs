using ResourceManager.App.Infrastructure.Adaptation;
using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal sealed partial class HostManagerTransactionJournalRuntime
{
    internal static HostManagerTransactionJournalRuntime Create(
        string hostManagerDataRoot,
        CompiledHostManagerTransactionJournalRecreatePlan recreatePlan,
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublishPlan,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        var configuration = CreateConfiguration(hostManagerDataRoot, recreatePlan, hotPublishPlan);
        return new HostManagerTransactionJournalRuntime(configuration, hotPublishPlan, createScratch);
    }

    private static HostManagerTransactionJournalRuntimeConfiguration CreateConfiguration(
        string hostManagerDataRoot,
        CompiledHostManagerTransactionJournalRecreatePlan recreatePlan,
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublishPlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostManagerDataRoot);
        ArgumentNullException.ThrowIfNull(recreatePlan);
        ValidateHotPublishPlan(hotPublishPlan, nameof(hotPublishPlan));
        if (!Path.IsPathFullyQualified(hostManagerDataRoot))
        {
            throw new ArgumentException(
                "The Host Manager data root must be an explicit absolute path.",
                nameof(hostManagerDataRoot));
        }
        if (!recreatePlan.IsPublished)
        {
            throw new ArgumentException(
                "The compiled transaction-journal recreate plan is not published.",
                nameof(recreatePlan));
        }
        var recordCapacity = checked((uint)recreatePlan.RecordCapacity);
        var residentByteBudget = checked((ulong)recreatePlan.ResidentByteBudget);
        var (root, journalPath, payloadDirectory) = ResolveStoragePaths(
            hostManagerDataRoot,
            recreatePlan);
        if (PathsOverlap(journalPath, payloadDirectory))
        {
            throw new ArgumentException(
                "The compiled journal path and payload directory must be disjoint.",
                nameof(recreatePlan));
        }

        return new HostManagerTransactionJournalRuntimeConfiguration(
            root,
            journalPath,
            payloadDirectory,
            recordCapacity,
            residentByteBudget,
            recreatePlan.PayloadCount,
            recreatePlan.PayloadByteBudget);
    }

    internal static (string DataRoot, string JournalPath, string PayloadDirectory)
        ResolveStoragePaths(
            string hostManagerDataRoot,
            CompiledHostManagerTransactionJournalRecreatePlan recreatePlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostManagerDataRoot);
        ArgumentNullException.ThrowIfNull(recreatePlan);
        if (!Path.IsPathFullyQualified(hostManagerDataRoot))
        {
            throw new ArgumentException(
                "The Host Manager data root must be an explicit absolute path.",
                nameof(hostManagerDataRoot));
        }
        if (!recreatePlan.IsPublished)
        {
            throw new ArgumentException(
                "The compiled transaction-journal recreate plan is not published.",
                nameof(recreatePlan));
        }

        var root = Path.GetFullPath(hostManagerDataRoot);
        var journalPath = ResolveStrictChildPath(
            root,
            recreatePlan.JournalRelativePath,
            nameof(recreatePlan.JournalRelativePath));
        var payloadDirectory = ResolveStrictChildPath(
            root,
            recreatePlan.PayloadRelativeDirectory,
            nameof(recreatePlan.PayloadRelativeDirectory));
        if (PathsOverlap(journalPath, payloadDirectory))
        {
            throw new ArgumentException(
                "The compiled journal path and payload directory must be disjoint.",
                nameof(recreatePlan));
        }
        return (root, journalPath, payloadDirectory);
    }

    private static void ValidateHotPublishPlan(
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublishPlan,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(hotPublishPlan, parameterName);
        if (!hotPublishPlan.IsPublished)
        {
            throw new ArgumentException(
                "The compiled transaction-journal hot-publish plan is not published.",
                parameterName);
        }
    }

    private static string ResolveStrictChildPath(
        string root,
        string relativePath,
        string parameterName)
    {
        try
        {
            return HostManagerDurableStorePaths.ResolveRelativePath(root, relativePath);
        }
        catch (InvalidDataException exception) { throw new ArgumentException(exception.Message, parameterName, exception); }
    }

    private static bool PathsOverlap(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
            IsStrictPathDescendant(first, second) ||
            IsStrictPathDescendant(second, first);

    private static bool IsStrictPathDescendant(string candidateParent, string candidateChild)
    {
        var prefix = Path.EndsInDirectorySeparator(candidateParent)
            ? candidateParent
            : candidateParent + Path.DirectorySeparatorChar;
        return candidateChild.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static (ulong Low, ulong High) CreateJournalInstanceIdentity()
    {
        Span<byte> bytes = stackalloc byte[16];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]);
            var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
            if (low != 0 || high != 0)
            {
                return (low, high);
            }
        }
    }

    private sealed record HostManagerTransactionJournalRuntimeConfiguration(
        string DataRoot,
        string JournalPath,
        string PayloadDirectory,
        uint RecordCapacity,
        ulong ResidentByteBudget,
        int PayloadCount,
        long PayloadByteBudget)
    {
        public NativeTransactionJournalCreateConfiguration CreateNativeConfiguration()
        {
            var journalInstance = CreateJournalInstanceIdentity();
            return new NativeTransactionJournalCreateConfiguration
            {
                AbiVersion = NativeTransactionJournalAbi.Version,
                StructSize = NativeTransactionJournalSession
                    .SizeOf<NativeTransactionJournalCreateConfiguration>(),
                JournalInstanceLow = journalInstance.Low,
                JournalInstanceHigh = journalInstance.High,
                RecordCapacity = RecordCapacity,
                Flags = 0,
                MaximumResidentBytes = ResidentByteBudget
            };
        }

        public NativeTransactionJournalOpenConfiguration CreateNativeOpenConfiguration()
            => new()
            {
                AbiVersion = NativeTransactionJournalAbi.Version,
                StructSize = NativeTransactionJournalSession
                    .SizeOf<NativeTransactionJournalOpenConfiguration>(),
                // Persisted capacity belongs to the durable image, not the new plan.
                // Keep the resident budget as the bound on opening it.
                MaximumRecordCapacity = checked((uint)Math.Min(uint.MaxValue,
                    ResidentByteBudget / NativeTransactionJournalSession
                        .SizeOf<NativeTransactionJournalRecord>())),
                Flags = 0,
                MaximumResidentBytes = ResidentByteBudget
            };
    }
}

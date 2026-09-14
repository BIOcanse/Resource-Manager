using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal sealed partial class HostManagerTransactionJournalRuntime
{
    public static async Task<HostManagerTransactionJournalRuntime> OpenAsync(
        string hostManagerDataRoot,
        CompiledHostManagerTransactionJournalRecreatePlan recreatePlan,
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublishPlan,
        CancellationToken cancellationToken)
    {
        var configuration = CreateConfiguration(
            hostManagerDataRoot,
            recreatePlan,
            hotPublishPlan);
        var runtime = new HostManagerTransactionJournalRuntime(
            configuration,
            hotPublishPlan);
        await runtime.InitializeAsync(cancellationToken);
        return runtime;
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
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\') ||
            relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':')
        {
            throw new ArgumentException(
                "The compiled transaction-journal path must be a canonical relative path.",
                parameterName);
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "The compiled transaction-journal path contains an invalid segment.",
                parameterName);
        }

        var resolved = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The compiled transaction-journal path escapes the Host Manager data root.",
                parameterName);
        }

        return resolved;
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
                MaximumRecordCapacity = RecordCapacity,
                Flags = 0,
                MaximumResidentBytes = ResidentByteBudget
            };
    }
}

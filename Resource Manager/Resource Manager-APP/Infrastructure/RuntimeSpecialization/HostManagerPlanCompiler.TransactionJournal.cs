using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private const uint TransactionJournalAbiVersion = 0x0005_0000;

    private static CompiledHostManagerTransactionJournalRecreatePlan CompileTransactionJournalRecreate(
        HostManagerTransactionJournalRecreateProfile source,
        CompiledHostManagerCapacityLimits limits)
    {
        ValidateCapacity(
            source.RecordCapacity,
            limits.TransactionJournalRecordCapacity,
            "host_recreate.transaction_journal.record_capacity");
        ValidatePositive(
            source.ResidentByteBudget,
            "host_recreate.transaction_journal.resident_byte_budget");
        ValidateMaximum(
            source.ResidentByteBudget,
            limits.TransactionJournalResidentByteBudget,
            "host_recreate.transaction_journal.resident_byte_budget");
        ValidateCapacity(
            source.PayloadCount,
            limits.TransactionJournalPayloadCount,
            "host_recreate.transaction_journal.payload_count");
        ValidatePositive(
            source.PayloadByteBudget,
            "host_recreate.transaction_journal.payload_byte_budget");
        ValidateMaximum(
            source.PayloadByteBudget,
            limits.TransactionJournalPayloadByteBudget,
            "host_recreate.transaction_journal.payload_byte_budget");

        var journalPath = NormalizeStrictRelativePath(
            source.JournalRelativePath,
            "host_recreate.transaction_journal.journal_relative_path");
        var payloadDirectory = NormalizeStrictRelativePath(
            source.PayloadRelativeDirectory,
            "host_recreate.transaction_journal.payload_relative_directory");
        ValidateDisjointPaths(journalPath, payloadDirectory);

        return new CompiledHostManagerTransactionJournalRecreatePlan(
            source.RecordCapacity,
            source.ResidentByteBudget,
            source.PayloadCount,
            source.PayloadByteBudget,
            journalPath,
            payloadDirectory);
    }

    private static CompiledHostManagerTransactionJournalHotPublishPlan CompileTransactionJournalHotPublish(
        HostManagerTransactionJournalHotPublishProfile source,
        int profileRevision)
    {
        ValidatePositive(
            source.MaximumRecoveryAttempts,
            "hot_publish.transaction_journal.maximum_recovery_attempts");
        ValidatePositive(
            source.RetryDelayMilliseconds,
            "hot_publish.transaction_journal.retry_delay_ms");
        ValidatePositive(
            source.RecoveryDeadlineMilliseconds,
            "hot_publish.transaction_journal.recovery_deadline_ms");
        ValidateNonNegative(
            source.MaximumFutureSkewMilliseconds,
            "hot_publish.transaction_journal.maximum_future_skew_ms");
        ValidatePositive(
            source.ShutdownDrainTimeoutMilliseconds,
            "hot_publish.transaction_journal.shutdown_drain_timeout_ms");

        return new CompiledHostManagerTransactionJournalHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.MaximumRecoveryAttempts,
            source.RetryDelayMilliseconds,
            source.RecoveryDeadlineMilliseconds,
            source.MaximumFutureSkewMilliseconds,
            source.ShutdownDrainTimeoutMilliseconds);
    }

    private static string NormalizeStrictRelativePath(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            throw new InvalidDataException($"{path} must be a non-empty relative path.");
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"{path} must not contain empty, current-directory, or parent-directory segments.");
        }

        return string.Join('/', segments);
    }

    private static void ValidateDisjointPaths(string journalPath, string payloadDirectory)
    {
        var journalSegments = journalPath.Split('/');
        var payloadSegments = payloadDirectory.Split('/');
        if (IsPathPrefix(journalSegments, payloadSegments)
            || IsPathPrefix(payloadSegments, journalSegments))
        {
            throw new InvalidDataException(
                "host_recreate.transaction_journal journal_relative_path and payload_relative_directory must not contain one another.");
        }
    }

    private static bool IsPathPrefix(string[] candidate, string[] value)
    {
        if (candidate.Length > value.Length)
        {
            return false;
        }

        for (var index = 0; index < candidate.Length; index++)
        {
            if (!string.Equals(candidate[index], value[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePositive(long value, string path)
    {
        if (value <= 0)
        {
            throw new InvalidDataException($"{path} must be greater than zero.");
        }
    }

    private static void ValidateMaximum(long value, long maximum, string path)
    {
        if (value > maximum)
        {
            throw new InvalidDataException($"{path} must not exceed its build_specialize capacity limit {maximum}.");
        }
    }
}

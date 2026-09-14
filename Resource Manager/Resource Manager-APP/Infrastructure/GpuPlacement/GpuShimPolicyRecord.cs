using System.Diagnostics.CodeAnalysis;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal sealed record GpuShimPolicyRecord(
    string TargetId,
    byte[]? PreviousValue,
    byte[] AppliedValue)
{
    internal static HostManagerAppliedRecord Create(string targetId, byte[]? previous, byte[] applied)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(applied);
        return new(HostManagerAppliedRecordKinds.GpuShimPolicy, targetId.Trim(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hadValue"] = previous is null ? "false" : "true",
                ["previousValue"] = previous is null ? string.Empty : Convert.ToBase64String(previous),
                ["appliedValue"] = Convert.ToBase64String(applied)
            });
    }

    internal static bool TryRead(HostManagerAppliedRecord record,
        [NotNullWhen(true)] out GpuShimPolicyRecord? policy)
    {
        policy = null;
        if (record.Kind != HostManagerAppliedRecordKinds.GpuShimPolicy
            || string.IsNullOrWhiteSpace(record.RecordId)
            || record.Metadata is not { } metadata
            || !metadata.TryGetValue("hadValue", out var hadValue)
            || hadValue is not ("true" or "false")
            || !metadata.TryGetValue("previousValue", out var previousToken)
            || previousToken is null
            || !metadata.TryGetValue("appliedValue", out var appliedToken)
            || appliedToken is null
            || (hadValue == "false" && previousToken.Length != 0))
        {
            return false;
        }

        try
        {
            var previous = hadValue == "true" ? Convert.FromBase64String(previousToken) : null;
            var applied = Convert.FromBase64String(appliedToken);
            // Equal values need no owned change and must not become a recovery record.
            if (previous is not null && previous.AsSpan().SequenceEqual(applied)) return false;
            policy = new(record.RecordId, previous, applied);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

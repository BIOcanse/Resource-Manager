using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationProtectionPlacementService
{
    private static bool MatchesActionIdentity(
        OptimizationProtectionPlacementActionPreview action,
        ProcessResourcePolicySnapshot current)
    {
        if (action.ProcessStartedAt is not null
            && current.StartedAt is not null
            && Math.Abs((current.StartedAt.Value - action.ProcessStartedAt.Value).TotalSeconds) > 1)
        {
            return false;
        }

        return PathsMatchOrNamesMatch(action.ExecutablePath, action.ProcessName, current.ExecutablePath, current.ProcessName);
    }

    private static bool MatchesRecordedIdentity(
        OptimizationProtectionPlacementAppliedAction action,
        ProcessResourcePolicySnapshot current)
    {
        if (action.ProcessStartedAt is not null
            && current.StartedAt is not null
            && Math.Abs((current.StartedAt.Value - action.ProcessStartedAt.Value).TotalSeconds) > 1)
        {
            return false;
        }

        return PathsMatchOrNamesMatch(action.ExecutablePath, action.ProcessName, current.ExecutablePath, current.ProcessName);
    }

    private static bool MatchesA2RecordedIdentity(
        OptimizationA2AppliedAction action,
        ProcessResourcePolicySnapshot current)
    {
        if (action.ProcessStartedAt is not null
            && current.StartedAt is not null
            && Math.Abs((current.StartedAt.Value - action.ProcessStartedAt.Value).TotalSeconds) > 1)
        {
            return false;
        }

        return PathsMatchOrNamesMatch(
            action.ExecutablePath,
            action.ProcessName ?? string.Empty,
            current.ExecutablePath,
            current.ProcessName);
    }

    private static bool PathsMatchOrNamesMatch(
        string? expectedPath,
        string expectedName,
        string? actualPath,
        string actualName)
    {
        var normalizedExpectedPath = NormalizePath(expectedPath);
        var normalizedActualPath = NormalizePath(actualPath);
        if (normalizedExpectedPath is not null && normalizedActualPath is not null)
        {
            return normalizedExpectedPath.Equals(normalizedActualPath, StringComparison.OrdinalIgnoreCase);
        }

        return expectedName.Equals(actualName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseMask(
        string? value,
        out long mask)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask)
            && mask > 0;
    }

    private static long BuildEnhancedMask(IReadOnlyList<OptimizationProtectionPlacementOwner> owners)
    {
        return owners.Aggregate(0L, static (current, owner) => current | owner.AffinityMask);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

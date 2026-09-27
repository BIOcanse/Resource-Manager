using System.Globalization;
using System.Text.RegularExpressions;

namespace ResourceManager.Shared.Packages;

/// <summary>A strict release ordering key. Unknown vendor formats remain visible but cannot authorize an upgrade.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool Stable => Prerelease.Count == 0;
    public string Series => Major == 0 ? $"0.{Minor}" : $"{Major}.x";

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        var match = Pattern.Match(value ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(segment => segment.Length == 0
            || (segment.Length > 1 && segment[0] == '0' && segment.All(char.IsDigit))))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Stable != other.Stable) return Stable ? 1 : -1;
        for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            var leftNumeric = left.All(char.IsDigit);
            var rightNumeric = right.All(char.IsDigit);
            result = leftNumeric && rightNumeric
                ? left.Length == right.Length
                    ? string.Compare(left, right, StringComparison.Ordinal)
                    : left.Length.CompareTo(right.Length)
                : leftNumeric ? -1 : rightNumeric ? 1 : string.Compare(left, right, StringComparison.Ordinal);
            if (result != 0) return result;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }
}

using System.Globalization;
using System.Text;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

/// <summary>Lossless source-specific selector helper. POST callers pass ordinary profileName strings.
/// Host still enforces its existing 8192-character path, 64-item and 600KiB body gates; no truncation.</summary>
public static class NvidiaDrsSubscriptionPath
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    public static string EncodeProfileName(string profileName)
    {
        ValidateName(profileName);
        return Convert.ToBase64String(StrictUnicode.GetBytes(profileName)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string DecodeProfileName(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length == 0 || token.Length > 5460
            || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new FormatException("Invalid DRS UTF-16 profile-name token.");
        var padded = token.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var bytes = Convert.FromBase64String(padded);
        if ((bytes.Length & 1) != 0) throw new FormatException("The DRS token has an incomplete UTF-16 code unit.");
        var name = StrictUnicode.GetString(bytes);
        ValidateName(name);
        if (!StringComparer.Ordinal.Equals(EncodeProfileName(name), token))
            throw new FormatException("The DRS profile token is not canonical base64url UTF-16LE.");
        return name;
    }

    public static string Create(NvidiaDrsProfileRequest profile, NvidiaDrsReadLimits limits, TimeSpan interval)
    {
        var request = new NvidiaDrsInformationRequest([profile], limits);
        if (request.Fields.IsEmpty) throw new ArgumentException("Select at least one DRS setting.", nameof(profile));
        var milliseconds = interval.TotalMilliseconds;
        if (milliseconds is < 100 or > 60_000 || milliseconds != Math.Truncate(milliseconds))
            throw new ArgumentOutOfRangeException(nameof(interval));
        var path = "/api/device-topology/nvidia-drs/fields/subscribe?profile=" + EncodeProfileName(profile.ProfileName)
            + "&settings=" + string.Join(',', request.Profiles[0].SettingIds.Order().Select(id => id.ToString("X8", CultureInfo.InvariantCulture)))
            + "&maxProfiles=" + limits.MaximumProfileCount.ToString(CultureInfo.InvariantCulture)
            + "&maxPerProfile=" + limits.MaximumSettingsPerProfile.ToString(CultureInfo.InvariantCulture)
            + "&maxTotalSettings=" + limits.MaximumTotalSettingCount.ToString(CultureInfo.InvariantCulture)
            + "&pageSize=" + limits.PageSize.ToString(CultureInfo.InvariantCulture)
            + "&maxPayloadBytes=" + limits.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture)
            + "&intervalMs=" + milliseconds.ToString("0", CultureInfo.InvariantCulture);
        if (path.Length > 8192) throw new ArgumentException("The complete DRS selector exceeds the Host path contract.", nameof(profile));
        return path;
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length >= 2048 || name.Contains('\0'))
            throw new ArgumentException("Profile lookup name must fit the NVAPI Unicode string.", nameof(name));
        _ = StrictUnicode.GetByteCount(name);
    }
}

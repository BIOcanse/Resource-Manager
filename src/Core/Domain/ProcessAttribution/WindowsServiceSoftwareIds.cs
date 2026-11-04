namespace ResourceManager.App.Domain.ProcessAttribution;

/// <summary>
/// The software identities of Windows services. A process that hosts exactly one service is attributed to
/// <c>windows-service:&lt;service name&gt;</c>; a process that hosts several is attributed to one
/// <c>windows-service-group:…</c> identity. The service name part is lower case with every run of
/// characters other than letters and digits replaced by one <c>-</c>.
/// </summary>
public static class WindowsServiceSoftwareIds
{
    public const string ServicePrefix = "windows-service:";
    public const string ServiceGroupPrefix = "windows-service-group:";

    public static string ForService(string serviceName) => ServicePrefix + NormalizePart(serviceName);

    public static string ForServiceGroup(IReadOnlyList<string> orderedServiceNames)
    {
        var identityText = string.Join('|', orderedServiceNames.Take(8));
        return $"{ServiceGroupPrefix}{NormalizePart(identityText)}-{orderedServiceNames.Count}";
    }

    public static string NormalizePart(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
    }
}

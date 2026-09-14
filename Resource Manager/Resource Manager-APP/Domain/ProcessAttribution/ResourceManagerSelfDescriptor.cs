using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Domain.ProcessAttribution;

public static class ResourceManagerSelfDescriptor
{
    public const string DisplayName = "资源管理器";
    public static readonly IReadOnlyList<AdapterCpuSchedulingGrade> SupportedCpuSchedulingGrades =
    [
        AdapterCpuSchedulingGrade.Normal,
        AdapterCpuSchedulingGrade.Optimize
    ];
    public static readonly IReadOnlyList<AdapterGpuSchedulingGrade> SupportedGpuSchedulingGrades =
    [
        AdapterGpuSchedulingGrade.Normal,
        AdapterGpuSchedulingGrade.Optimize
    ];

    public static readonly string[] ProcessNames =
    [
        "ResourceManager",
        "ResourceManager.NativeUi"
    ];

    public static IReadOnlyList<string> ResolveRootPaths()
    {
        var roots = new List<string>();
        AddNormalizedRoot(roots, AppContext.BaseDirectory);

        var backendRoot = NormalizePath(AppContext.BaseDirectory);
        if (backendRoot is not null)
        {
            var binRoot = Directory.GetParent(backendRoot);
            if (binRoot is not null)
            {
                AddNormalizedRoot(roots, Path.Combine(binRoot.FullName, "ResourceManagerNativeUi"));
            }
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void AddNormalizedRoot(List<string> roots, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is not null)
        {
            roots.Add(normalized);
        }
    }
}

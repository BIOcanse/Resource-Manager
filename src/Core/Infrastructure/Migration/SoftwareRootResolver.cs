using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Infrastructure.Migration;

public sealed class SoftwareRootResolver(
    IOptionalDependencyManager dependencyManager,
    IAdapterSoftwareRegistry adapterRegistry,
    IControlledSoftwareRegistry controlledRegistry) : ISoftwareRootResolver
{
    public async Task<SoftwareRootResolution> ResolveAsync(
        SoftwareRootResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var sources = new Dictionary<string, SoftwareRootResolutionSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in NormalizeRootPaths(request.ExplicitRootPaths))
        {
            sources[root] = new SoftwareRootResolutionSource(
                "Explicit",
                "request",
                "手动根目录",
                root,
                "请求中显式提供。");
        }

        foreach (var status in await dependencyManager.GetStatusesAsync(cancellationToken))
        {
            if (!IsDependencyMatch(status, request))
            {
                continue;
            }

            var root = NormalizeRootPath(status.EffectiveInstallDirectory);
            if (root is null || !Directory.Exists(root))
            {
                continue;
            }

            sources[root] = new SoftwareRootResolutionSource(
                "OptionalDependency",
                status.Definition.Id,
                status.Definition.Name,
                root,
                "匹配托管可选依赖安装根目录。");
        }

        foreach (var registration in await adapterRegistry.GetAllAsync(cancellationToken))
        {
            if (!IsAdapterRegistrationMatch(registration, request))
            {
                continue;
            }

            foreach (var root in NormalizeRootPaths(registration.ProgramRootPaths))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                sources[root] = new SoftwareRootResolutionSource(
                    "AdapterRegistration",
                    registration.Id,
                    registration.DisplayName,
                    root,
                    "匹配适配软件持久注册根目录。");
            }
        }

        foreach (var registration in controlledRegistry.GetAll())
        {
            if (!IsControlledRegistrationMatch(registration, request))
            {
                continue;
            }

            foreach (var root in NormalizeRootPaths(registration.ProgramRootPaths))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                sources[root] = new SoftwareRootResolutionSource(
                    "ControlledRegistration",
                    registration.Id.ToString("D"),
                    registration.Software.FirstOrDefault() ?? registration.Command,
                    root,
                    "匹配适配/受控软件注册根目录。");
            }
        }

        return new SoftwareRootResolution(
            sources.Keys.OrderBy(static item => item).ToArray(),
            sources.Values.OrderBy(static item => item.SourceType).ThenBy(static item => item.RootPath).ToArray());
    }

    private static bool IsDependencyMatch(OptionalDependencyStatus status, SoftwareRootResolutionRequest request)
    {
        var tokens = Tokenize(request.SoftwareName)
            .Concat(request.ProcessNames.SelectMany(Tokenize))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
        {
            return false;
        }

        return MatchesAnyToken(status.Definition.Id, tokens)
            || MatchesAnyToken(status.Definition.Name, tokens)
            || MatchesAnyToken(status.Definition.Vendor, tokens);
    }

    private static bool IsControlledRegistrationMatch(
        ControlledSoftwareRegistration registration,
        SoftwareRootResolutionRequest request)
    {
        var tokens = Tokenize(request.SoftwareName)
            .Concat(request.ProcessNames.SelectMany(Tokenize))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
        {
            return registration.ProgramRootPaths.Count > 0;
        }

        return MatchesAnyToken(registration.Command, tokens)
            || registration.Software.Any(item => MatchesAnyToken(item, tokens))
            || registration.Processes.Any(item => MatchesAnyToken(item.Name, tokens)
                || MatchesAnyToken(Path.GetFileNameWithoutExtension(item.ExecutablePath ?? string.Empty), tokens));
    }

    private static bool IsAdapterRegistrationMatch(
        AdapterSoftwareRegistration registration,
        SoftwareRootResolutionRequest request)
    {
        var tokens = Tokenize(request.SoftwareName)
            .Concat(request.ProcessNames.SelectMany(Tokenize))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
        {
            return registration.ProgramRootPaths.Count > 0;
        }

        return MatchesAnyToken(registration.AdapterId, tokens)
            || MatchesAnyToken(registration.AppId, tokens)
            || MatchesAnyToken(registration.DisplayName, tokens)
            || registration.Processes.Any(item => MatchesAnyToken(item.Name, tokens)
                || MatchesAnyToken(Path.GetFileNameWithoutExtension(item.ExecutablePath ?? string.Empty), tokens));
    }

    private static IReadOnlyList<string> NormalizeRootPaths(IReadOnlyList<string>? rootPaths)
    {
        if (rootPaths is null)
        {
            return [];
        }

        return rootPaths
            .Select(NormalizeRootPath)
            .Where(static item => item is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootPath.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([' ', '-', '_', '.', '(', ')', '[', ']', '\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => item.Length >= 2)
            .Select(static item => item.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesAnyToken(string? value, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

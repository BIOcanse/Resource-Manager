using System.Collections.Concurrent;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Infrastructure.Migration.Etw;

namespace ResourceManager.App.Infrastructure.Migration;

public sealed partial class SoftwareDataDiscoveryManager(
    IFirstRunFileTraceFactory traceFactory,
    ISoftwareRootResolver rootResolver) : ISoftwareDataDiscoveryManager, IDisposable
{
    private static readonly string[] IgnoredTopLevelSegments =
    [
        "Temp",
        "Microsoft",
        "Packages",
        "CrashDumps",
        "ConnectedDevicesPlatform",
        "D3DSCache"
    ];

    private readonly ConcurrentDictionary<string, DiscoverySessionState> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<DiscoveryRoot> roots = CreateDefaultRoots();

    public IReadOnlyList<SoftwareDataDiscoveryCandidate> FindNameCandidates(SoftwareDataDiscoveryRequest request)
    {
        var tokens = Tokenize(request.SoftwareName);
        if (tokens.Count == 0)
        {
            return [];
        }

        var candidates = new Dictionary<string, SoftwareDataDiscoveryCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(static item => Directory.Exists(item.Path)))
        {
            foreach (var directory in EnumerateDirectories(root.Path, maxDepth: 2, maxCount: 4000))
            {
                var name = Path.GetFileName(directory);
                if (!MatchesTokens(name, tokens))
                {
                    continue;
                }

                candidates[directory] = new SoftwareDataDiscoveryCandidate(
                    directory,
                    root.Path,
                    "NameMatch",
                    root.TargetCategory,
                    "Data",
                    0.7,
                    0,
                    DateTimeOffset.Now,
                    "路径名称与软件名匹配。",
                    "NameScan",
                    [],
                    []);
            }
        }

        return candidates.Values
            .OrderByDescending(static item => item.Confidence)
            .ThenBy(static item => item.Path)
            .ToArray();
    }

    public IReadOnlyList<SoftwareDataDiscoverySession> GetSessions()
    {
        return sessions.Values.Select(static session => session.ToDto()).OrderByDescending(static item => item.StartedAt).ToArray();
    }

    public async Task<SoftwareDataDiscoverySession> StartSessionAsync(
        SoftwareDataDiscoveryStartRequest request,
        CancellationToken cancellationToken)
    {
        var softwareName = string.IsNullOrWhiteSpace(request.SoftwareName)
            ? "UnknownSoftware"
            : request.SoftwareName.Trim();
        var processNames = NormalizeProcessNames(request.ProcessNames);
        var rootResolution = await rootResolver.ResolveAsync(
            new SoftwareRootResolutionRequest(softwareName, request.ProgramRootPaths, processNames),
            cancellationToken);
        var programRootPaths = NormalizeProgramRootPaths(rootResolution.RootPaths);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..34];
        var session = new DiscoverySessionState(id, softwareName, processNames, programRootPaths, rootResolution.Sources, roots, traceFactory.Create());

        if (!sessions.TryAdd(id, session))
        {
            throw new InvalidOperationException("无法创建发现会话。");
        }

        session.Start();
        return session.ToDto();
    }

    public SoftwareDataDiscoverySession? StopSession(string id)
    {
        if (!sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        session.Stop("手动停止。");
        return session.ToDto();
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values)
        {
            session.Stop("服务关闭。");
        }

        sessions.Clear();
    }

    private static IReadOnlyList<DiscoveryRoot> CreateDefaultRoots()
    {
        var defaultRoots = new DiscoveryRoot[]
        {
            new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UserData"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UserData"),
            new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"), "UserData"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Misc"),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UserData")
        };

        return defaultRoots
            .Where(root => Directory.Exists(root.Path) && IsOnSystemDrive(root.Path))
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeProcessNames(IReadOnlyList<string>? processNames)
    {
        if (processNames is null)
        {
            return [];
        }

        return processNames
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => Path.GetFileNameWithoutExtension(item.Trim()))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeProgramRootPaths(IReadOnlyList<string>? programRootPaths)
    {
        if (programRootPaths is null)
        {
            return [];
        }

        return programRootPaths
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => Path.GetFullPath(Environment.ExpandEnvironmentVariables(item.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return value
            .Split([' ', '-', '_', '.', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => item.Length >= 2)
            .Select(static item => item.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesTokens(string name, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.ToLowerInvariant();
        return tokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOnSystemDrive(string path)
    {
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windowsRoot);
        var pathDrive = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(systemDrive)
            && pathDrive?.Equals(systemDrive, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth, int maxCount)
    {
        var yielded = 0;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && yielded < maxCount)
        {
            var current = queue.Dequeue();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current.Path);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                yielded++;
                yield return child;

                if (current.Depth + 1 < maxDepth)
                {
                    queue.Enqueue((child, current.Depth + 1));
                }

                if (yielded >= maxCount)
                {
                    yield break;
                }
            }
        }
    }
}

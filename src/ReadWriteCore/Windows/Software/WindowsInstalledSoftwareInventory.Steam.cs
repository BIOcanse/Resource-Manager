using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Software;

public sealed partial class WindowsInstalledSoftwareInventory
{
    internal static bool IsSteamClientId(string id)
        => id.StartsWith("windows-installed:", StringComparison.OrdinalIgnoreCase)
            && id.EndsWith("-uninstall-steam", StringComparison.OrdinalIgnoreCase);

    internal static ulong? SteamAppId(string id)
    {
        var match = Regex.Match(id, @"^(?:steam-app-|windows-installed:.+-uninstall-steam-app-)([1-9][0-9]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }

    // An uninstall dispatcher is owned by Steam, never by every game invoking it.
    internal static IReadOnlyList<string> SteamGameRoots(string id, IReadOnlyList<string> roots, IReadOnlySet<string> clientRoots)
        => SteamAppId(id) is null ? roots
            : roots.Where(root => NormalizePath(root) is not { } path || !clientRoots.Contains(path)).ToArray();

    internal static string? SteamGameRootForExecutable(string path)
    {
        var normalized = path.Replace('\\', '/');
        const string segment = "/steamapps/common/";
        var start = normalized.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += segment.Length;
        var end = normalized.IndexOf('/', start);
        return end > start ? normalized[..end] : null;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Steam library manifest changed")]
    internal static IReadOnlyList<InstalledSoftwareEntry> ReadSteamLibraries(
        IReadOnlyList<InstalledSoftwareEntry> entries, CancellationToken cancellationToken)
    {
        var clientRoots = entries.Where(entry => IsSteamClientId(entry.Id)).SelectMany(entry => entry.RootPaths)
            .Select(path => NormalizePath(path)).Where(path => path is not null).Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = entries.Select(entry => entry with { RootPaths = SteamGameRoots(entry.Id, entry.RootPaths, clientRoots) }).ToList();
        var libraries = new HashSet<string>(clientRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var client in clientRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadInventoryEntry(client, unused =>
            {
                var file = Path.Combine(client, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(file)) return;
                var folders = ReadSteamKeyValues(file).Children.GetValueOrDefault("libraryfolders");
                if (folders is null) return;
                foreach (var pair in folders.Children.Take(256))
                {
                    if (!uint.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                    var path = pair.Value.Children.GetValueOrDefault("path")?.Value ?? pair.Value.Value;
                    if (path is not null && Path.IsPathFullyQualified(path) && !path.Contains('%')
                        && NormalizePath(path) is { } library) libraries.Add(library);
                }
            });
        }
        var manifestsRead = 0;
        foreach (var library in libraries.Take(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadInventoryEntry(library, unused =>
            {
                var steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) return;
                foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++manifestsRead > 16384) throw new InvalidDataException("Steam manifest count exceeds inventory limit.");
                    ReadInventoryEntry(manifest, file =>
                    {
                        var state = ReadSteamKeyValues(file).Children.GetValueOrDefault("AppState");
                        if (state is null) return;
                        var appText = state.Children.GetValueOrDefault("appid")?.Value;
                        if (!ulong.TryParse(appText, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0
                            || !Path.GetFileName(file).Equals($"appmanifest_{appId}.acf", StringComparison.OrdinalIgnoreCase)) return;
                        var name = state.Children.GetValueOrDefault("name")?.Value;
                        var directory = state.Children.GetValueOrDefault("installdir")?.Value;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory)
                            || directory is "." or ".." || directory.IndexOfAny(['/', '\\', ':']) >= 0
                            || directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
                        var gameRoot = Path.GetFullPath(Path.Combine(steamapps, "common", directory));
                        if (!Directory.Exists(gameRoot)) return;
                        var index = result.FindIndex(entry => SteamAppId(entry.Id) == appId);
                        if (index >= 0)
                        {
                            var entry = result[index];
                            result[index] = entry with { InstallLocation = gameRoot,
                                RootPaths = entry.RootPaths.Append(gameRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                        }
                        else result.Add(new InstalledSoftwareEntry($"steam-app-{appId}", name.Trim(), null, null,
                            gameRoot, [gameRoot], null, file));
                    });
                }
            });
        }
        return result;
    }

    private sealed class SteamKeyValue
    {
        internal string? Value;
        internal Dictionary<string, SteamKeyValue> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // Parse Valve KeyValues iteratively with bounded input and nesting.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Steam library manifest changed")]
    private static SteamKeyValue ReadSteamKeyValues(string path)
    {
        const int maximumCharacters = 4 * 1024 * 1024;
        using var reader = new StreamReader(path);
        var buffer = new char[(int)Math.Min(new FileInfo(path).Length + 1, maximumCharacters + 1L)];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > maximumCharacters) throw new InvalidDataException("Steam manifest exceeds inventory limit.");
        var text = new string(buffer, 0, length);
        var root = new SteamKeyValue();
        var stack = new Stack<SteamKeyValue>();
        stack.Push(root);
        string? key = null;
        var offset = 0;
        var tokens = 0;
        while (offset < text.Length)
        {
            if (++tokens > 100000) throw new InvalidDataException("Steam manifest exceeds token limit.");
            var current = text[offset++];
            if (char.IsWhiteSpace(current)) continue;
            if (current == '/' && offset < text.Length && text[offset] == '/')
            {
                while (offset < text.Length && text[offset] != '\n') offset++;
                continue;
            }
            if (current == '{')
            {
                if (key is null || stack.Count >= 64) throw new InvalidDataException("Invalid Steam block.");
                var child = new SteamKeyValue();
                if (!stack.Peek().Children.TryAdd(key, child)) throw new InvalidDataException("Duplicate Steam key.");
                stack.Push(child);
                key = null;
            }
            else if (current == '}')
            {
                if (key is not null || stack.Count == 1) throw new InvalidDataException("Invalid Steam block end.");
                stack.Pop();
            }
            else if (current == '"')
            {
                var value = new StringBuilder();
                var closed = false;
                while (offset < text.Length)
                {
                    current = text[offset++];
                    if (current == '"') { closed = true; break; }
                    if (current == '\\' && offset < text.Length && text[offset] is '\\' or '"') current = text[offset++];
                    value.Append(current);
                }
                if (!closed) throw new InvalidDataException("Unterminated Steam string.");
                if (key is null) key = value.ToString();
                else
                {
                    if (!stack.Peek().Children.TryAdd(key, new SteamKeyValue { Value = value.ToString() }))
                        throw new InvalidDataException("Duplicate Steam key.");
                    key = null;
                }
            }
            else throw new InvalidDataException("Invalid Steam token.");
        }
        if (key is not null || stack.Count != 1) throw new InvalidDataException("Incomplete Steam manifest.");
        return root;
    }
}

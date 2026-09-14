using ResourceManager.App.Application.NetworkTelemetry;
using System.Text.Json;

namespace ResourceManager.App.Infrastructure.NetworkTelemetry;

public sealed class KnownNetworkProxyCatalog : IKnownNetworkProxyCatalog
{
    private const string RelativeSignaturePath = "Infrastructure/Resources/NetworkProxySignatures/proxy-signatures.v1.json";

    private readonly HashSet<string> processNames;
    private readonly HashSet<int> ports;

    public KnownNetworkProxyCatalog(IHostEnvironment environment)
    {
        var snapshot = LoadSnapshot(environment);
        processNames = snapshot.ProcessNames;
        ports = snapshot.Ports;
    }

    public bool IsKnownProxyProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        return processNames.Contains(normalized);
    }

    public bool IsLikelyProxyPort(int port)
    {
        return ports.Contains(port);
    }

    private static ProxySignatureSnapshot LoadSnapshot(IHostEnvironment environment)
    {
        var path = ResolveSignaturePath(environment);
        if (path is not null)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var document = JsonSerializer.Deserialize<ProxySignatureDocument>(stream, JsonSerializerOptions.Web);
                if (document?.Proxies is { Count: > 0 })
                {
                    return CreateSnapshot(document.Proxies);
                }
            }
            catch
            {
                // Keep runtime monitoring alive when the optional data file is missing or malformed.
            }
        }

        return CreateSnapshot(FallbackProxies());
    }

    private static string? ResolveSignaturePath(IHostEnvironment environment)
    {
        var candidates = new[]
        {
            Path.Combine(environment.ContentRootPath, RelativeSignaturePath),
            Path.Combine(AppContext.BaseDirectory, RelativeSignaturePath),
            Path.Combine(Directory.GetCurrentDirectory(), RelativeSignaturePath)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static ProxySignatureSnapshot CreateSnapshot(IReadOnlyList<ProxySignatureEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signaturePorts = new HashSet<int>();
        foreach (var entry in entries)
        {
            foreach (var processName in entry.ProcessNames ?? [])
            {
                var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    names.Add(normalized);
                }
            }

            foreach (var port in entry.DefaultListenPorts ?? [])
            {
                if (port > 0 && port <= ushort.MaxValue)
                {
                    signaturePorts.Add(port);
                }
            }
        }

        return new ProxySignatureSnapshot(names, signaturePorts);
    }

    private static IReadOnlyList<ProxySignatureEntry> FallbackProxies()
    {
        return
        [
            new ProxySignatureEntry(
                "fallback-local-proxy",
                "Fallback Local Proxy",
                ["clash", "mihomo", "v2ray", "xray", "sing-box", "nekoray", "proxifier", "privoxy", "gost"],
                [1080, 10808, 10809, 2080, 7890, 7891, 7892, 7893, 8080, 8118, 8888, 9090])
        ];
    }

    private sealed record ProxySignatureSnapshot(HashSet<string> ProcessNames, HashSet<int> Ports);

    public sealed record ProxySignatureDocument(string Version, IReadOnlyList<ProxySignatureEntry> Proxies);

    public sealed record ProxySignatureEntry(
        string Id,
        string DisplayName,
        IReadOnlyList<string>? ProcessNames,
        IReadOnlyList<int>? DefaultListenPorts);
}

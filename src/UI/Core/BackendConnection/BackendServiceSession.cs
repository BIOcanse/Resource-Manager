using ResourceManager.NativeUi.Localization;
using System.Net.Http;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.NativeUi;

/// <summary>
/// The window process's direct transport to the backend. The resident owns the backend connection and publishes
/// the backend-session fact over the pipe; this session only pins the installed service's process identity and
/// carries the page's requests through the loopback proxy.
/// </summary>
internal sealed class BackendServiceSession : IDisposable, IAsyncDisposable
{
    private const string ApiKeyHeaderName = TrustedAccessHeaders.KeyHeader;

    private readonly HttpClient frontendClient;
    private readonly Uri endpoint;
    private readonly Func<string?> readApiKey;
    private readonly Func<BackendProcessIdentity?> captureIdentity;
    private BackendProcessIdentity? backendProcessIdentity;
    private readonly object disposalSync = new();
    private Task? generationEndCompletion;
    private Task? disposalCompletion;
    private int holdCount = 1;
    private bool generationEnded;
    private bool disposed;

    internal BackendServiceSession(string baseAddress,
        Func<string?>? apiKeyReader = null,
        Func<BackendProcessIdentity?>? identityReader = null,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        endpoint = new Uri(baseAddress);
        BackendPath = ResolveBackendPath();
        readApiKey = apiKeyReader ?? NativeUiBackendCredential.ForCurrentProcess(NativeUiPaths.PackageRoot);
        captureIdentity = identityReader ?? (() => BackendProcessConnection.CaptureInstalledServiceIdentity(BackendPath));
        frontendClient = new HttpClient(handlerFactory?.Invoke()
            ?? BackendProcessConnection.CreateHandler(() => backendProcessIdentity), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string BackendPath { get; }
    internal Uri ApiBaseAddress => endpoint;

    internal Hold? TryAcquire()
    {
        lock (disposalSync)
        {
            if (generationEnded) return null;
            holdCount = checked(holdCount + 1);
            return new Hold(this);
        }
    }

    internal sealed class Hold(BackendServiceSession session) : IDisposable
    {
        private BackendServiceSession? session = session;
        internal BackendServiceSession Session => session ?? throw new ObjectDisposedException(nameof(Hold));
        public void Dispose() => Interlocked.Exchange(ref session, null)?.ReleaseHold();
    }

    public static BackendServiceSession CreateDefault(string baseAddress)
    {
        return new BackendServiceSession(baseAddress);
    }

    // Parent publishes readiness. The window attests its own direct transport without
    // creating another health monitor or publishing a competing backend session.
    internal void PrepareWindowTransport()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        backendProcessIdentity = captureIdentity()
            ?? throw new InvalidOperationException(NativeUiText.Current.BackendNotOwned);
    }

    internal Task<HttpResponseMessage> SendFrontendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.RequestUri is not { IsAbsoluteUri: true } address
            || address.Scheme != endpoint.Scheme || address.Authority != endpoint.Authority
            || address.UserInfo.Length != 0)
            throw new UnauthorizedAccessException("The frontend request does not target the configured backend.");
        // Static documents use the same pinned peer without depending on a business key.
        ApplyRequestAuthorization(request, includeApiKey:
            address.AbsolutePath.Equals("/api", StringComparison.OrdinalIgnoreCase)
            || address.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase));
        return frontendClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }


    public void Dispose()
        => _ = BeginDispose();

    public async ValueTask DisposeAsync()
        => await BeginDispose().ConfigureAwait(false);

    internal Task EndAsync()
    {
        lock (disposalSync)
        {
            if (generationEndCompletion is not null) return generationEndCompletion;
            generationEnded = true;
            backendProcessIdentity = null;
            generationEndCompletion = Task.CompletedTask;
            return generationEndCompletion;
        }
    }

    private Task BeginDispose()
    {
        lock (disposalSync)
        {
            if (disposalCompletion is not null) return disposalCompletion;
            disposalCompletion = ReleaseOwnerAsync(EndAsync());
            return disposalCompletion;
        }
    }

    private async Task ReleaseOwnerAsync(Task generationEnd)
    {
        try { await generationEnd.ConfigureAwait(false); }
        finally { ReleaseHold(); }
    }

    private void ReleaseHold()
    {
        lock (disposalSync)
        {
            if (--holdCount != 0) return;
            disposed = true;
        }
        frontendClient.Dispose();
    }

    internal static string ResolveBackendPath()
    {
        var candidates = ResolveBackendPathCandidates();
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IReadOnlyList<string> ResolveBackendPathCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "ResourceManager.exe"),
            Path.Combine(baseDirectory, "..", "ResourceManager", "ResourceManager.exe")
        };

        if (TryFindAppRoot(baseDirectory, out var appRoot))
        {
            foreach (var configuration in ResolveBuildConfigurations(baseDirectory))
            {
                candidates.Add(Path.Combine(appRoot, "bin", configuration, "net10.0-windows", "ResourceManager.exe"));
                candidates.Add(Path.Combine(appRoot, "bin", configuration, "net10.0", "ResourceManager.exe"));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveBuildConfigurations(string baseDirectory)
    {
        var parts = baseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(static part => part.Equals("Release", StringComparison.OrdinalIgnoreCase))
            ? ["Release", "Debug"]
            : ["Debug", "Release"];
    }

    // Source layout: the shell locates the backend; the backend never resolves shell paths.
    private static bool TryFindAppRoot(string startPath, out string appRoot)
    {
        for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "ResourceManager.NativeUi.csproj")))
            {
                continue;
            }

            var backendRoot = Path.GetFullPath(Path.Combine(directory.FullName, "..", "..", "Core"));
            if (File.Exists(Path.Combine(backendRoot, "ResourceManager.App.csproj")))
            {
                appRoot = backendRoot;
                return true;
            }

            break;
        }

        appRoot = string.Empty;
        return false;
    }

    private void ApplyRequestAuthorization(HttpRequestMessage request, bool includeApiKey = true)
    {
        if (backendProcessIdentity is null)
            throw new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady);
        if (!includeApiKey) return;
        var apiKey = readApiKey()
            ?? throw new IOException("The default access key file is unavailable.");
        // An empty, validated file deliberately disables the default credential.
        // Leave authorization to the backend; read/validation failures still propagate.
        if (apiKey.Length != 0) request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);
    }
}

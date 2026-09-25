using ResourceManager.NativeUi.Localization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

internal sealed class BackendServiceSession : IBackendServiceAvailabilitySource, IDisposable
{
    private const string AccessTokenHeaderName = "X-Resource-Manager-Token";
    private const int MaximumProbeResponseBytes = 16 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions ProbeJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly HttpClient probeClient;
    private readonly HttpClient actionClient;
    private BackendRequestAuthorization? requestAuthorization;
    private BackendServiceAvailabilityMonitor? availabilityMonitor;
    private bool disposed;

    private BackendServiceSession(string baseAddress)
    {
        var endpoint = new Uri(baseAddress);
        probeClient = new HttpClient(CreateLoopbackHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = ProbeTimeout
        };
        actionClient = new HttpClient(CreateLoopbackHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(30)
        };
        BackendPath = ResolveBackendPath();
    }

    public string BackendPath { get; }

    public string? AccessToken => requestAuthorization?.AccessToken;

    public event EventHandler<BackendServiceUnavailableEventArgs>? AvailabilityLost;

    public static BackendServiceSession CreateDefault(string baseAddress)
    {
        return new BackendServiceSession(baseAddress);
    }

    public async Task<string> TerminateProcessesAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (processIds.Count == 0)
        {
            return NativeUiText.Current.NoProcessToTerminate;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/processes/terminate")
        {
            Content = JsonContent.Create(new { processIds })
        };
        GetRequestAuthorization().Apply(request);
        using var response = await actionClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadMessage(payload) ?? string.Format(NativeUiText.Current.BackendHttpErrorFormat, (int)response.StatusCode));
        }
        return ReadMessage(payload) ?? NativeUiText.Current.TerminateRequestCompleted;
    }

    public async Task<string> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (await TryConnectAsync(cancellationToken))
        {
            StartAvailabilityMonitor();
            return NativeUiText.Current.StatusReady;
        }

        if (!File.Exists(BackendPath))
        {
            throw new FileNotFoundException(
                NativeUiText.Current.BackendEntryMissing,
                BackendPath);
        }

        throw new InvalidOperationException(
            NativeUiText.Current.BackendNotOwned);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (availabilityMonitor is not null)
        {
            availabilityMonitor.AvailabilityLost -= OnAvailabilityLost;
            availabilityMonitor.Dispose();
            availabilityMonitor = null;
        }

        probeClient.Dispose();
        actionClient.Dispose();
        requestAuthorization = null;
    }

    private Task<bool> TryConnectAsync(
        CancellationToken cancellationToken) =>
        ProbeBackendAsync(requireCurrentToken: false, cancellationToken);

    private Task<bool> ProbeCurrentConnectionAsync(
        CancellationToken cancellationToken) =>
        ProbeBackendAsync(requireCurrentToken: true, cancellationToken);

    private async Task<bool> ProbeBackendAsync(
        bool requireCurrentToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = ReadAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken)
                || requireCurrentToken
                    && !string.Equals(
                        requestAuthorization?.AccessToken,
                        accessToken,
                        StringComparison.Ordinal))
            {
                return false;
            }

            var challenge = RandomNumberGenerator.GetHexString(
                LoopbackSessionProofContract.ChallengeHexLength);
            using var proofRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{LoopbackSessionProofContract.EndpointPath}?challenge={challenge}");
            using var proofResponse = await probeClient.SendAsync(
                proofRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (proofResponse.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var proof = await ReadBoundedJsonAsync<LoopbackSessionProofResponse>(
                proofResponse,
                cancellationToken);
            if (proof is null
                || !string.Equals(
                    proof.ProductId,
                    LoopbackSessionProofContract.ProductId,
                    StringComparison.Ordinal)
                || proof.ProtocolVersion != LoopbackSessionProofContract.ProtocolVersion
                || !string.Equals(proof.Challenge, challenge, StringComparison.Ordinal)
                || !LoopbackSessionProofContract.VerifyProof(
                    accessToken,
                    challenge,
                    proof.Proof))
            {
                return false;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/runtime/capabilities");
            request.Headers.TryAddWithoutValidation(
                AccessTokenHeaderName,
                accessToken);

            using var response = await probeClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var capabilities = await ReadBoundedJsonAsync<BackendStartupCapabilitiesResponse>(
                response,
                cancellationToken);
            if (capabilities is null
                || string.IsNullOrWhiteSpace(capabilities.ProfileId))
            {
                return false;
            }

            requestAuthorization = BackendRequestAuthorization.ForToken(accessToken);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpClientHandler CreateLoopbackHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false
    };

    private static async Task<T?> ReadBoundedJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength is > MaximumProbeResponseBytes)
        {
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var payload = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await responseStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (payload.Length + read > MaximumProbeResponseBytes)
            {
                return null;
            }

            payload.Write(buffer, 0, read);
        }

        return JsonSerializer.Deserialize<T>(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length)),
            ProbeJsonOptions);
    }

    private void StartAvailabilityMonitor()
    {
        if (availabilityMonitor is not null)
        {
            return;
        }

        availabilityMonitor = new BackendServiceAvailabilityMonitor(
            ProbeCurrentConnectionAsync,
            waitForOwnedProcessExitAsync: null);
        availabilityMonitor.AvailabilityLost += OnAvailabilityLost;
        availabilityMonitor.Start();
    }

    private void OnAvailabilityLost(
        object? sender,
        BackendServiceUnavailableEventArgs unavailable)
    {
        if (!disposed)
        {
            requestAuthorization = null;
            AvailabilityLost?.Invoke(this, unavailable);
        }
    }

    private static string? ReadAccessToken()
    {
        try
        {
            if (!File.Exists(NativeUiPaths.LoopbackApiTokenPath))
            {
                return null;
            }

            var token = File.ReadAllText(NativeUiPaths.LoopbackApiTokenPath).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveBackendPath()
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

    private static bool TryFindAppRoot(string startPath, out string appRoot)
    {
        for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ResourceManager.App.csproj")))
            {
                appRoot = directory.FullName;
                return true;
            }
        }

        appRoot = string.Empty;
        return false;
    }

    private BackendRequestAuthorization GetRequestAuthorization() =>
        requestAuthorization
        ?? throw new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady);

    private sealed record BackendRequestAuthorization(
        string? AccessToken,
        Action<HttpRequestMessage> Apply)
    {
        public static BackendRequestAuthorization ForToken(string accessToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
            return new BackendRequestAuthorization(
                accessToken,
                request => request.Headers.TryAddWithoutValidation(
                    AccessTokenHeaderName,
                    accessToken));
        }
    }
}

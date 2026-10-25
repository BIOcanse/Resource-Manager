using ResourceManager.NativeUi.Localization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

namespace ResourceManager.NativeUi;

internal sealed class BackendServiceSession : IBackendServiceAvailabilitySource, IDisposable
{
    private const string ApiKeyHeaderName = TrustedAccessHeaders.KeyHeader;
    private const int MaximumProbeResponseBytes = 16 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly NativeBackendProbeJsonContext ProbeJsonContext = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        });

    private readonly HttpClient probeClient;
    private readonly HttpClient actionClient;
    private readonly HttpClient desktopPublisherClient;
    private readonly HttpClient streamClient;
    private readonly HttpClient frontendClient;
    private readonly Uri endpoint;
    private readonly Func<string?> readApiKey;
    private readonly Func<BackendProcessIdentity?> captureIdentity;
    private BackendProcessIdentity? backendProcessIdentity;
    private BackendServiceAvailabilityMonitor? availabilityMonitor;
    private bool disposed;

    internal BackendServiceSession(string baseAddress,
        Func<string?>? apiKeyReader = null,
        Func<BackendProcessIdentity?>? identityReader = null)
    {
        endpoint = new Uri(baseAddress);
        BackendPath = ResolveBackendPath();
        readApiKey = apiKeyReader ?? new NativeTrustedAccessKeyReader(NativeUiPaths.PackageRoot).Read;
        captureIdentity = identityReader ?? (() => BackendProcessConnection.CaptureInstalledServiceIdentity(BackendPath));
        HttpMessageHandler CreateHandler() => BackendProcessConnection.CreateHandler(() => backendProcessIdentity);
        probeClient = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = ProbeTimeout
        };
        actionClient = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(30)
        };
        desktopPublisherClient = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(30)
        };
        streamClient = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        frontendClient = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string BackendPath { get; }

    public event EventHandler<BackendServiceUnavailableEventArgs>? AvailabilityLost;

    public static BackendServiceSession CreateDefault(string baseAddress)
    {
        return new BackendServiceSession(baseAddress);
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

    internal async Task<HttpResponseMessage> OpenPerformanceOverlayStreamAsync(
        ResourceManager.NativeUi.Overlay.OverlaySubscription[] subscriptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new
            {
                version = 1,
                subscriptions
            })
        };
        ApplyRequestAuthorization(request);
        var response = await streamClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        var statusCode = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException($"Overlay stream returned {(int)statusCode}.");
    }


    internal async Task<ResourceManager.NativeUi.Overlay.StartupModuleOperationsDocument> ReadStartupModulesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/startup-modules/settings");
        ApplyRequestAuthorization(request);
        using var response = await actionClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync(ResourceManager.NativeUi.Overlay.OverlayStartupModulesJson.Default.StartupModuleOperationsDocument,
            cancellationToken) ?? throw new InvalidDataException("Missing startup module operations.");
        if (document.Version != 1 || document.Software is null) throw new InvalidDataException("Invalid startup module operations.");
        return document;
    }

    public async Task<string> TerminateProcessesAsync(
        IReadOnlyList<ForceTerminateProcessTarget> targets,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (targets.Count == 0)
        {
            return NativeUiText.Current.NoProcessToTerminate;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/processes/terminate")
        {
            Content = JsonContent.Create(new { targets })
        };
        ApplyRequestAuthorization(request);
        using var response = await actionClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden && RequiresProcessElevation(payload))
                return SummarizeTermination(await ExecuteElevatedProcessActionAsync(new("terminate",
                    targets.Select(target => new ProcessActionTarget(target.ProcessId, target.ProcessStartKey)).ToArray()),
                    cancellationToken), NativeUiText.Current);
            throw new InvalidOperationException(ReadMessage(payload) ?? string.Format(NativeUiText.Current.BackendHttpErrorFormat, (int)response.StatusCode));
        }
        return SummarizeTermination(payload, NativeUiText.Current);
    }

    internal static string SummarizeTermination(string payload, NativeText text)
    {
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.GetProperty("action").GetString() != "Terminate")
            throw new InvalidDataException("The process result action does not match the termination request.");
        var items = json.RootElement.GetProperty("items");
        var succeeded = 0;
        foreach (var item in items.EnumerateArray())
        {
            var outcome = item.GetProperty("outcome").GetString();
            var expectedState = outcome switch
            {
                "Terminated" => "Succeeded",
                "BackendProtected" or "CriticalProcessProtected" or "IdentityChanged" or "ProcessExited" => "Skipped",
                "AccessDenied" or "NativeFailure" or "FileFailure" or "RequestRejected" => "Failed",
                _ => throw new InvalidDataException("The termination outcome is unknown.")
            };
            if (item.GetProperty("state").GetString() != expectedState)
                throw new InvalidDataException("The process state and outcome disagree.");
            if (outcome == "Terminated") succeeded++;
        }
        return string.Format(text.TerminateResultFormat, succeeded, items.GetArrayLength());
    }

    internal Task<string> ExecuteElevatedProcessActionAsync(ProcessActionBatch batch, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var backend = backendProcessIdentity ?? throw new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady);
        return ElevatedProcessActionBroker.ExecuteAsync(batch, backend, cancellationToken);
    }

    internal Task<ElevatedBackendResponse> ExecuteElevatedBackendRequestAsync(ElevatedBackendMutation mutation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var backend = backendProcessIdentity ?? throw new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady);
        return ElevatedProcessActionBroker.ExecuteBackendRequestAsync(mutation, backend, cancellationToken);
    }

    internal static bool RequiresProcessElevation(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            var value = root.TryGetProperty("error", out var error)
                ? error.GetString()
                : root.TryGetProperty("code", out var code) ? code.GetString() : null;
            return value is "access-scope-required" or "elevated-administrator-required" or "ProcessActionRequiresElevation";
        }
        catch (JsonException) { return false; }
    }

    internal async Task PublishDesktopAsync(InteractiveDesktopObservation observation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using (var select = new HttpRequestMessage(HttpMethod.Post,
            "/api/runtime/interactive-desktop/publisher/select"))
        {
            ApplyRequestAuthorization(select);
            using var selected = await desktopPublisherClient.SendAsync(select, cancellationToken);
            if (selected.StatusCode != HttpStatusCode.NoContent)
                throw new HttpRequestException($"Interactive desktop publisher selection returned {(int)selected.StatusCode}.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, InteractiveDesktopObservation.EndpointPath)
        { Content = JsonContent.Create(observation) };
        ApplyRequestAuthorization(request);
        using var response = await desktopPublisherClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
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
        desktopPublisherClient.Dispose();
        streamClient.Dispose();
        frontendClient.Dispose();
        backendProcessIdentity = null;
    }

    private Task<bool> TryConnectAsync(
        CancellationToken cancellationToken) =>
        ProbeBackendAsync(requireCurrentProcess: false, cancellationToken);

    private Task<bool> ProbeCurrentConnectionAsync(
        CancellationToken cancellationToken) =>
        ProbeBackendAsync(requireCurrentProcess: true, cancellationToken);

    private async Task<bool> ProbeBackendAsync(
        bool requireCurrentProcess,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = captureIdentity();
            if (identity is null
                || requireCurrentProcess && !SameProcess(identity, backendProcessIdentity))
            {
                return false;
            }
            backendProcessIdentity = identity;

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/runtime/capabilities");

            using var response = await probeClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var capabilities = await ReadBoundedJsonAsync(
                response,
                ProbeJsonContext.BackendStartupCapabilitiesResponse,
                cancellationToken);
            if (capabilities is null
                || string.IsNullOrWhiteSpace(capabilities.ProfileId))
            {
                return false;
            }

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
        catch (Win32Exception)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<T?> ReadBoundedJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> jsonTypeInfo,
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

        return JsonSerializer.Deserialize(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length)),
            jsonTypeInfo);
    }

    private static bool SameProcess(BackendProcessIdentity first, BackendProcessIdentity? second)
        => second is not null
            && first.ProcessId == second.ProcessId
            && first.CreatedUtcTicks == second.CreatedUtcTicks
            && StringComparer.OrdinalIgnoreCase.Equals(first.ExecutablePath, second.ExecutablePath);

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
            backendProcessIdentity = null;
            AvailabilityLost?.Invoke(this, unavailable);
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

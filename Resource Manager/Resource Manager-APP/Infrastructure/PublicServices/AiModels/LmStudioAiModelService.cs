using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.PublicServices.AiModels;

public sealed class LmStudioAiModelService(
    HttpClient httpClient,
    IAppSettingsStore settingsStore,
    ILogger<LmStudioAiModelService> logger) : IAiModelRuntimeProvider
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim startGate = new(1, 1);

    public async Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var running = await ProbeAsync(settings, cancellationToken);
        return new AiModelRuntimeStatus(
            "lm-studio",
            settings.Endpoint,
            CliAvailable: LmStudioCliLocator.Find() is not null,
            ServerRunning: running,
            settings.AutoStartEnabled,
            running ? null : "LM Studio server is not running.");
    }

    public async Task<IReadOnlyList<AiModelDescriptor>> ListModelsIfRunningAsync(
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        using var response = await SendAsync(
            settings,
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/models"),
            ensureRunning: false,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models.EnumerateArray().Select(ParseModel).ToArray();
    }

    public async Task<AiModelLoadResult> LoadAsync(
        AiModelLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var settings = await LoadSettingsAsync(cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.Trim()
        };
        Add(payload, "context_length", request.ContextLength);
        Add(payload, "eval_batch_size", request.EvalBatchSize);
        Add(payload, "flash_attention", request.FlashAttention);
        Add(payload, "offload_kv_cache_to_gpu", request.OffloadKvCacheToGpu);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/models/load")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await SendAsync(
            settings,
            message,
            ensureRunning: true,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new AiModelLoadResult(
            ReadString(root, "type") ?? "llm",
            ReadString(root, "instance_id") ?? request.Model.Trim(),
            ReadDouble(root, "load_time_seconds") ?? 0,
            ReadString(root, "status") ?? "loaded");
    }

    public async Task<AiModelUnloadResult> UnloadAsync(
        AiModelUnloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
        var settings = await LoadSettingsAsync(cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/models/unload")
        {
            Content = JsonContent.Create(new { instance_id = request.InstanceId.Trim() })
        };
        using var response = await SendAsync(
            settings,
            message,
            ensureRunning: true,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new AiModelUnloadResult(
            ReadString(document.RootElement, "instance_id") ?? request.InstanceId.Trim());
    }

    public async Task ForwardAsync(
        HttpContext context,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var providerPath = relativePath + context.Request.QueryString;
        using var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), providerPath);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            message.Content = new StreamContent(context.Request.Body);
        }

        CopyRequestHeaders(context.Request, message);
        using var response = await SendAsync(
            settings,
            message,
            ensureRunning: true,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response);
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        AppAiModelServiceSettings settings,
        HttpRequestMessage request,
        bool ensureRunning,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        if (ensureRunning)
        {
            await EnsureRunningAsync(settings, cancellationToken);
        }

        request.RequestUri = new Uri(new Uri(settings.Endpoint, UriKind.Absolute), request.RequestUri!);
        AddProviderAuthorization(request);
        try
        {
            return await httpClient.SendAsync(request, completionOption, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AiModelRuntimeUnavailableException("LM Studio is not reachable.", exception);
        }
    }

    private async Task EnsureRunningAsync(
        AppAiModelServiceSettings settings,
        CancellationToken cancellationToken)
    {
        if (await ProbeAsync(settings, cancellationToken))
        {
            return;
        }

        if (!settings.AutoStartEnabled)
        {
            throw new AiModelRuntimeUnavailableException("LM Studio auto-start is disabled.");
        }

        await startGate.WaitAsync(cancellationToken);
        try
        {
            if (await ProbeAsync(settings, cancellationToken))
            {
                return;
            }

            var cliPath = LmStudioCliLocator.Find()
                ?? throw new AiModelRuntimeUnavailableException("LM Studio CLI was not found.");
            var endpoint = new Uri(settings.Endpoint, UriKind.Absolute);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = $"server start --port {endpoint.Port} --bind 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new AiModelRuntimeUnavailableException("Unable to start the LM Studio CLI.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await ProbeAsync(settings, cancellationToken))
                {
                    return;
                }

                await Task.Delay(250, cancellationToken);
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            logger.LogWarning("LM Studio server failed to start: {Error}", error);
            throw new AiModelRuntimeUnavailableException("LM Studio did not become ready after startup.");
        }
        finally
        {
            startGate.Release();
        }
    }

    private async Task<bool> ProbeAsync(
        AppAiModelServiceSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(settings.Endpoint, UriKind.Absolute), "/api/v1/models"));
        AddProviderAuthorization(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<AppAiModelServiceSettings> LoadSettingsAsync(CancellationToken cancellationToken) =>
        (await settingsStore.LoadAsync(cancellationToken)).Settings.AiModelService;

    private static AiModelDescriptor ParseModel(JsonElement model)
    {
        var loadedInstances = model.TryGetProperty("loaded_instances", out var instances)
            && instances.ValueKind == JsonValueKind.Array
            ? instances.EnumerateArray().Select(ParseInstance).ToArray()
            : [];
        return new AiModelDescriptor(
            ReadString(model, "type") ?? "unknown",
            ReadString(model, "publisher") ?? string.Empty,
            ReadString(model, "key") ?? string.Empty,
            ReadString(model, "display_name") ?? ReadString(model, "key") ?? string.Empty,
            ReadString(model, "architecture"),
            ReadString(model, "format"),
            model.TryGetProperty("quantization", out var quantization)
                ? ReadString(quantization, "name")
                : null,
            model.TryGetProperty("quantization", out quantization)
                ? ReadInt32(quantization, "bits_per_weight")
                : null,
            ReadInt64(model, "size_bytes") ?? 0,
            ReadString(model, "params_string"),
            ReadInt32(model, "max_context_length"),
            ParseCapabilities(model),
            loadedInstances);
    }

    private static AiModelLoadedInstance ParseInstance(JsonElement instance) =>
        new(
            ReadString(instance, "instance_id")
                ?? ReadString(instance, "id")
                ?? string.Empty,
            ReadInt32(instance, "context_length"));

    private static IReadOnlyList<string> ParseCapabilities(JsonElement model)
    {
        if (!model.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return capabilities.EnumerateObject()
            .Where(static property => property.Value.ValueKind != JsonValueKind.False
                && property.Value.ValueKind != JsonValueKind.Null)
            .Select(static property => property.Name)
            .ToArray();
    }

    private static void Add(IDictionary<string, object?> payload, string key, object? value)
    {
        if (value is not null)
        {
            payload[key] = value;
        }
    }

    private static void AddProviderAuthorization(HttpRequestMessage request)
    {
        var token = Environment.GetEnvironmentVariable("LM_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage target)
    {
        foreach (var header in source.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "X-Resource-Manager-Token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                target.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"LM Studio returned {(int)response.StatusCode}: {body}",
            inner: null,
            response.StatusCode);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;
}

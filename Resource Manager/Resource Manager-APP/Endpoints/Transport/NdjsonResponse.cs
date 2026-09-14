using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;

namespace ResourceManager.App.Endpoints.Transport;

internal static class NdjsonResponse
{
    public static void Prepare(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.HttpContext.Features
            .Get<IHttpResponseBodyFeature>()?
            .DisableBuffering();
    }

    public static async Task WriteAsync<T>(
        HttpResponse response,
        T value,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(
            response.Body,
            value,
            serializerOptions,
            cancellationToken);
        await response.WriteAsync("\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

}

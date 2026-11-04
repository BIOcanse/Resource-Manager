using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    // H-cutover deletes these exact-route Kestrel metadata/transport entries.
    // Method/path come from the native family, no binding or DTO send remains.
    private static void MapNativeRuntimeReadEndpoints(this IEndpointRouteBuilder app)
    {
        uint kind = 0;
        while (kind < 4)
        {
            var selected = kind++;
            app.MapGet(NativeRuntimeHttpBinding.PathFor(selected), async (HttpContext context) =>
            {
                if (!await NativeHttpCommonEntry.TryInvokeAsync(context))
                    throw new InvalidOperationException("The registered native runtime family is unavailable.");
            }).AllowAnonymous();
        }
    }
}

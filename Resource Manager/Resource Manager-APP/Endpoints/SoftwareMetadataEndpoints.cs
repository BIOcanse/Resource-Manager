using ResourceManager.App.Application.SoftwareMetadata;
using ResourceManager.App.Domain.SoftwareMetadata;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapSoftwareMetadataEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/software/metadata/{softwareIdentityId}", (
            string softwareIdentityId,
            string? language,
            ISoftwareMetadataCatalog metadataCatalog) =>
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return Results.BadRequest(new
                {
                    message = "A resolved language tag is required."
                });
            }

            try
            {
                var metadata = metadataCatalog.Resolve(
                    softwareIdentityId,
                    language);
                return Results.Ok(new SoftwareMetadataLookupResult(
                    metadataCatalog.Version,
                    metadata is not null,
                    metadata));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).AllowAnonymous();

        return app;
    }
}

using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static void MapNvidiaDrsInformationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/device-topology/nvidia-drs/fields/current", IResult (NvidiaDrsFieldRequest? request,
            HttpResponse response, [Microsoft.AspNetCore.Mvc.FromServices] DeviceTopologySnapshotProvider provider) =>
        {
            DisableResponseCache(response);
            try
            {
                var scope = DrsScope(request);
                var access = provider.CreateNvidiaDrsInformation(scope);
                return Results.Ok(scope.Fields.Select(access.Backend.ReadCached).ToImmutableArray());
            }
            catch (ArgumentException error) { return Results.BadRequest(new EndpointErrorPayload(error.Message)); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(SubscriptionChannelMaximumBodyLength));

        app.MapPost("/api/device-topology/nvidia-drs/fields/read", async Task<IResult> (NvidiaDrsFieldRequest? request,
            HttpResponse response, [Microsoft.AspNetCore.Mvc.FromServices] DeviceTopologySnapshotProvider provider, CancellationToken token) =>
        {
            DisableResponseCache(response);
            try { return Results.Ok(await provider.ReadNvidiaDrsInformationAsync(DrsScope(request), token)); }
            catch (ArgumentException error) { return Results.BadRequest(new EndpointErrorPayload(error.Message)); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(SubscriptionChannelMaximumBodyLength));

        app.MapPost("/api/device-topology/nvidia-drs/fields/discover", async Task<IResult> (NvidiaDrsReadLimits? limits,
            HttpResponse response, [Microsoft.AspNetCore.Mvc.FromServices] DeviceTopologySnapshotProvider provider, CancellationToken token) =>
        {
            DisableResponseCache(response);
            try
            {
                ArgumentNullException.ThrowIfNull(limits);
                var observed = await provider.DiscoverNvidiaDrsInformationAsync(limits, token);
                return observed is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(observed);
            }
            catch (ArgumentException error) { return Results.BadRequest(new EndpointErrorPayload(error.Message)); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(SubscriptionChannelMaximumBodyLength));
    }

    private static NvidiaDrsInformationRequest DrsScope(NvidiaDrsFieldRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Profiles);
        ArgumentNullException.ThrowIfNull(request.Limits);
        NvidiaNvapiReader.ValidateDrsLimits(request.Limits);
        var count = request.Profiles.Count;
        if (count < 0 || count > request.Limits.MaximumProfileCount) throw new ArgumentOutOfRangeException(nameof(request));
        var profiles = new NvidiaDrsProfileRequest[count];
        for (var index = 0; index < count; index++)
        {
            var profile = request.Profiles[index] ?? throw new ArgumentException("A profile selection is null.");
            profiles[index] = new(profile.ProfileName, profile.SettingIds ?? throw new ArgumentException("settingIds is required."));
        }
        if (request.Profiles.Count != count) throw new ArgumentException("Profile count changed during request copy.");
        return new(profiles, request.Limits);
    }
}

internal sealed record NvidiaDrsFieldRequest(IReadOnlyList<NvidiaDrsProfileFieldRequest?>? Profiles, NvidiaDrsReadLimits? Limits);
internal sealed record NvidiaDrsProfileFieldRequest(
    string ProfileName, IReadOnlyList<uint>? SettingIds);

using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace Resource_Manager_APP.Tests;

public sealed class GpuVfCurveEndpointTests
{
    [Fact]
    public void SourceGeneratedHttpMetadataCoversAllGpuVfPayloadsAndImmutableExecution()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var descriptor = GpuVfCurveServiceTests.Descriptor();
        var command = new GpuVfCurveEditorCommand("move-selection", descriptor.ObjectId, descriptor.Binding!,
            "draft", PointIds: [7, 19], DeltaKhz: 1000);
        var decoded = JsonSerializer.Deserialize<GpuVfCurveEditorCommand>(JsonSerializer.Serialize(command, options), options)!;
        Assert.Equal(command.Operation, decoded.Operation);
        Assert.Equal(command.PointIds, decoded.PointIds);
        Assert.Equal(command.ExpectedBinding, decoded.ExpectedBinding);
        Assert.Contains("\"scalarOnly\":false", JsonSerializer.Serialize(descriptor, options));
        var points = descriptor.Points.Select(p => new GpuVfCurvePoint(p.PointId, p.VoltageMicrovolts, p.CurrentOffsetKhz)).ToArray();
        var setting = new ControlSetting(GpuVfCurveCapabilities.Core, GpuVfCurvePresetId: "named",
            GpuVfCurveExecution: new(descriptor.Binding!, points));
        var restored = JsonSerializer.Deserialize<ControlSetting>(JsonSerializer.Serialize(setting, options), options)!;
        Assert.Equal(points, restored.GpuVfCurveExecution!.Points);
        foreach (var type in new[] { typeof(GpuVfCurveSaveRequest), typeof(GpuVfCurveDraft), typeof(GpuVfCurvePreset),
            typeof(GpuVfCurveLibraryView), typeof(GpuVfCurveReferencesPayload), typeof(GpuVfCurveClosedPayload),
            typeof(GpuVfCurveDeletedPayload), typeof(GpuVfCurveErrorPayload) })
            Assert.NotNull(options.GetTypeInfo(type));
    }

    [Fact]
    public async Task ReadOnlyStartupMapsDescriptorsAndReferencesButNoEditorOrPersistenceMutations()
    {
        using var fixture = new GpuVfCurveServiceTests.Fixture();
        await using var app = BuildApp(fixture, StartupCapabilitySet.NormalReadOnly);
        var routes = Routes(app);
        Assert.Contains(routes, route => route.RoutePattern.RawText == "/api/control/objects/{objectId}/gpu-vf-curve");
        Assert.Contains(routes, route => route.RoutePattern.RawText == "/api/control/gpu-vf-curves/{curveId}/references");
        Assert.DoesNotContain(routes, route => route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Any(method => method != "GET"));
        var endpoint = routes.Single(route => route.RoutePattern.RawText == "/api/control/objects/{objectId}/gpu-vf-curve");
        var context = Context(app, "GET");
        context.Request.RouteValues["objectId"] = "gpu:a";
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        using var json = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(7, json.RootElement.GetProperty("points")[0].GetProperty("pointId").GetInt32());
    }

    [Fact]
    public async Task SingleEditorPostUsesBackendDraftAndRejectsBindingChangeAsJson()
    {
        using var fixture = new GpuVfCurveServiceTests.Fixture();
        await using var app = BuildApp(fixture, StartupCapabilitySet.Full);
        var endpoint = Routes(app).Single(route => route.RoutePattern.RawText == "/api/control/gpu-vf-curves/editor/command");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var context = Context(app, "POST");
        var command = new GpuVfCurveEditorCommand("begin", "gpu:a", fixture.Provider.Value.Binding!);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, options));
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        using var json = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.False(json.RootElement.GetProperty("canUndo").GetBoolean());
        Assert.Equal(15000, json.RootElement.GetProperty("points")[0].GetProperty("frequencyOffsetKhz").GetDouble());
        var rejected = Context(app, "POST");
        var invalid = command with { ExpectedBinding = command.ExpectedBinding with { DeviceIdentity = "replacement" } };
        var invalidBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(invalid, options));
        rejected.Request.ContentType = "application/json";
        rejected.Request.ContentLength = invalidBody.Length;
        rejected.Request.Body = new MemoryStream(invalidBody);
        rejected.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
        await endpoint.RequestDelegate!(rejected);
        Assert.Equal(400, rejected.Response.StatusCode);
        using var failure = JsonDocument.Parse(((MemoryStream)rejected.Response.Body).ToArray());
        Assert.Equal("gpu-vf-curve-binding-changed", failure.RootElement.GetProperty("reason").GetProperty("code").GetString());
    }

    private static WebApplication BuildApp(GpuVfCurveServiceTests.Fixture fixture, StartupCapabilitySet capabilities)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options => ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<IGpuVfCurveDescriptorProvider>(fixture.Provider);
        builder.Services.AddSingleton<IGpuVfCurvePresets>(fixture.Service);
        builder.Services.AddSingleton<IGpuVfCurveEditor>(fixture.Service);
        var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapGpuVfCurveEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app, capabilities]);
        return app;
    }

    private static RouteEndpoint[] Routes(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
    private static DefaultHttpContext Context(WebApplication app, string method)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        return context;
    }
    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}

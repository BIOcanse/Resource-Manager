using ResourceManager.App.Application.Control;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder app)
    {
        // 可控对象清单。每次现读：显卡会热插拔，风扇会因为装上组件而突然可控。
        //
        // 控不了的对象和能力**照样返回**，带上原因和缺哪个组件。
        // 这和指标目录是同一条口径 —— 过滤掉等于把算好的原因扔掉，
        // 用户看到的就是"根本没这功能"，分不清是机器不支持还是软件没做。
        app.MapGet("/api/control/objects", (
            HttpResponse response,
            IControlObjectCatalog catalog) =>
        {
            DisableResponseCache(response);
            return Results.Ok(catalog.ReadObjects());
        }).AllowAnonymous();

        return app;
    }
}

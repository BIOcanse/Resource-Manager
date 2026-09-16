using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

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

        // 期望状态 + 最近一次施加的回执。界面据此算"已应用/正在应用/没应用上"。
        app.MapGet("/api/control/state", async (
            HttpResponse response,
            IControlPlane plane,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await plane.ReadStateAsync(cancellationToken));
        }).AllowAnonymous();

        // 改某个对象的设定。**先存后施加**，所以返回的一定是存下来的那份。
        // 施加不算重大操作：用户设一次就该一直维持，不每次问。
        app.MapPut("/api/control/state/{objectId}", async (
            string objectId,
            ControlSetting[] settings,
            IControlPlane plane,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await plane.SetObjectSettingsAsync(
                    objectId,
                    settings ?? [],
                    cancellationToken));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        return app;
    }
}

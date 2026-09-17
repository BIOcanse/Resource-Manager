using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Endpoints;

/// <summary>用户对超频免责声明的答复。</summary>
public sealed record ControlOverclockConsentRequest(bool Accepted);

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

        // 实际状态：这台机器现在实际是什么样。
        //
        // **它不是期望状态的回声**：固件会按温度自己调度，用户也可能用别的软件改过，
        // 两者对不上是常态，而且正是用户需要看见的信息。
        // 只返回当前值，不触发采样 —— 采样是后台那条独立的路。
        app.MapGet("/api/control/actual", (
            HttpResponse response,
            IControlActualStateOwner owner) =>
        {
            DisableResponseCache(response);
            return Results.Ok(owner.Current);
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

        // 整份应用。草稿应用和配置应用走同一条路 —— 用户面对的是一整套设定，
        // 不是一条条分别提交。这一份里没有的项会被恢复到硬件默认。
        app.MapPut("/api/control/state", async (
            ControlDesiredState? desired,
            IControlPlane plane,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await plane.ApplyDesiredStateAsync(
                    desired ?? ControlDesiredState.Empty,
                    cancellationToken));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // 超频免责声明的同意状态。
        //
        // **这一条不是我们发明的流程，是厂商的硬性要求**：Intel 的 IGCL 在用户
        // 接受之前拒绝所有超频接口，原文写着设置它表示用户接受部件寿命缩短，
        // 并要求应用先告知用户。所以界面上要先把后果说清楚，用户点了同意才调这个。
        app.MapGet("/api/control/overclock-consent", async (
            HttpResponse response,
            IControlOverclockConsent consent,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(new
            {
                accepted = await consent.IsAcceptedAsync(cancellationToken)
            });
        }).AllowAnonymous();

        app.MapPut("/api/control/overclock-consent", async (
            ControlOverclockConsentRequest? request,
            IControlOverclockConsent consent,
            CancellationToken cancellationToken) =>
        {
            await consent.SetAcceptedAsync(request?.Accepted == true, cancellationToken);
            return Results.Ok(new
            {
                accepted = await consent.IsAcceptedAsync(cancellationToken)
            });
        });

        // 配置：**绑定实例**的几套方案。选中一个实例就能看到为它存过的那几份。
        //
        // **这里没有"应用某份配置"这样的动作。** 点一份配置是把它的内容载入草稿，
        // 那一步只发生在前端；真要落到硬件，走上面那条整份应用 —— 应用只有一条路。
        // 多一条"直接应用配置"的捷径，就会有两处定义"应用是什么"。
        app.MapGet("/api/control/presets", async (
            HttpResponse response,
            IControlPresets presets,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await presets.ReadAsync(cancellationToken));
        }).AllowAnonymous();

        // 给某个实例存一份。同一个实例下同名覆盖 —— 在这块卡上再存一次"游戏"
        // 是想更新那一份；不同实例下的同名配置互不相干。
        app.MapPut("/api/control/presets/{objectId}/{name}", async (
            string objectId,
            string name,
            ControlSetting[]? settings,
            IControlPresets presets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await presets.SaveAsync(
                    objectId,
                    name,
                    settings ?? [],
                    cancellationToken));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        app.MapDelete("/api/control/presets/{presetId}", async (
            string presetId,
            IControlPresets presets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await presets.DeleteAsync(presetId, cancellationToken));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // 见过的设备登记表。在场的标激活，不在场的照样列出来 ——
        // 「都能看到连接过什么东西」。
        app.MapGet("/api/control/instances", async (
            HttpResponse response,
            IControlInstanceRegistry registry,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await registry.ReadAsync(cancellationToken));
        }).AllowAnonymous();

        // 重新检测。新设备会被登记并自带默认配置。
        app.MapPost("/api/control/instances/refresh", async (
            IControlInstanceRegistry registry,
            CancellationToken cancellationToken) =>
            Results.Ok(await registry.RefreshAsync(cancellationToken)));

        // 删掉一条早就不用的记录，连同它的设定。
        app.MapDelete("/api/control/instances/{instanceId}", async (
            string instanceId,
            IControlInstanceRegistry registry,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await registry.ForgetAsync(instanceId, cancellationToken));
            }
            catch (InvalidOperationException error)
            {
                // 设备还在场：说清楚为什么删不了，而不是点了没反应。
                return Results.BadRequest(new { error = error.Message });
            }
        });

        return app;
    }
}

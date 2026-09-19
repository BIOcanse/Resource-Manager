using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Endpoints;

/// <summary>用户对超频免责声明的答复。</summary>
public sealed record ControlOverclockConsentRequest(bool Accepted);

/// <summary>切到哪一档调节权限。认不出来的值按最低那一档处理。</summary>
public sealed record ControlAccessLevelRequest(string? Level);

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

        /*
         * 某个对象现在跑的曲线。
         *
         * **单独一条路，不进对象清单。** 读一条曲线要把固件的三张表都过一遍，
         * 几十次 EC 往返；对象清单是打开控制页就要拉一次的，混进去整页都得等。
         * 所以只在用户真去看曲线的时候才走这里。
         *
         * 读不到就回 204：**"读不到"和"读到一条空曲线"必须能分开** ——
         * 后者会让界面画出一条水平在 0% 的线，看着像风扇被我们关了。
         */
        app.MapGet("/api/control/objects/{objectId}/curve", async (
            string objectId,
            HttpResponse response,
            IControlObjectCatalog catalog,
            IEnumerable<IControlWriter> writers,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var target = catalog.ReadObjects().Objects
                .FirstOrDefault(entry => string.Equals(
                    entry.Id,
                    objectId,
                    StringComparison.Ordinal));
            if (target is null)
            {
                return Results.NotFound();
            }
            foreach (var writer in writers)
            {
                if (await writer.ReadCurveAsync(target, cancellationToken).ConfigureAwait(false)
                    is { } curve)
                {
                    return Results.Ok(curve);
                }
            }
            return Results.NoContent();
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

        /*
         * 重新检测这台机器。
         *
         * 探测要碰硬件（问辅助进程、起风扇核心、用空写问驱动），所以都记了缓存。
         * 缓存一记，用户装上组件、插上显卡之后就看不到变化，只能重启程序。
         * 这条路把缓存清掉，下一次列可控对象时重新问一遍。
         *
         * **只清缓存，不改任何设定。** 重新检测不该顺手把用户调过的东西动了。
         */
        app.MapPost("/api/control/redetect", (
            IEnumerable<IControlDetectionCache> caches,
            IControlObjectCatalog catalog) =>
        {
            foreach (var cache in caches)
            {
                cache.ResetDetection();
            }
            return Results.Ok(catalog.ReadObjects());
        });

        /*
         * 首次须知看过没有。第一次进控制页要把保修和风险说清楚，说过一次就不再拦。
         *
         * 和上面那个超频同意不是一回事：那个是 Intel IGCL 的硬性要求、必须可以收回；
         * 这个只是"我看过了"，没有收回一说。
         */
        app.MapGet("/api/control/notice", async (
            HttpResponse response,
            IControlNoticeAcknowledgement notice,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(new
            {
                acknowledged = await notice.IsAcknowledgedAsync(cancellationToken)
            });
        }).AllowAnonymous();

        app.MapPut("/api/control/notice", async (
            IControlNoticeAcknowledgement notice,
            CancellationToken cancellationToken) =>
        {
            await notice.AcknowledgeAsync(cancellationToken);
            return Results.Ok(new { acknowledged = true });
        });

        /*
         * 调节权限档位：安全 / 普通 / root。每一项能力声明自己要哪一档。
         *
         * **这不是一道安全闸。** 程序本来就要管理员才起得来，想调的人在设置里
         * 两下就切到最高档。它管的是别手滑点到危险的项，以及让用户看得出哪些项危险。
         *
         * 和 Intel 核显那个豁免是两件事：那个是厂商 API 的硬性要求，
         * 调它的频率偏移哪怕是负向的都要先接受。
         */
        app.MapGet("/api/control/access-level", (
            HttpResponse response,
            IControlAccessLevel accessLevel) =>
        {
            DisableResponseCache(response);
            return Results.Ok(new
            {
                level = accessLevel.Current,
                levels = ControlAccessLevels.All
            });
        }).AllowAnonymous();

        app.MapPut("/api/control/access-level", async (
            ControlAccessLevelRequest? request,
            IControlAccessLevel accessLevel,
            CancellationToken cancellationToken) =>
        {
            await accessLevel.SetAsync(
                request?.Level ?? ControlAccessLevels.Normal,
                cancellationToken);
            return Results.Ok(new { level = accessLevel.Current });
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

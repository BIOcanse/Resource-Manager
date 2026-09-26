using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// AMD SMU 的现场事实：PM table 版本、长度、以及这个版本有没有做过索引映射。
    ///
    /// 有它才回答得了"电压电流为什么没有"这类问题 ——
    /// 先前只能看到一句"尚未完成索引映射"，却看不到到底是哪个版本没映射。
    /// 诊断用，不进监控链路。
    /// </summary>
    private static IEndpointRouteBuilder MapDebugAmdSmuEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/amd-smu", (IHostEnvironment environment) =>
        {
            using var reader = new AmdSmuCpuSensorReader(environment.ContentRootPath);
            return Results.Ok(reader.DescribeSession());
        }).AllowAnonymous();

        return app;
    }
}

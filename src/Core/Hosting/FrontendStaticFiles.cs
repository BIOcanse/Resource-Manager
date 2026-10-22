using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;

namespace ResourceManager.App.Hosting;

public static class FrontendStaticFiles
{
    // 页面文件每次都向后端确认（带 ETag，未变时 304）。脚本和样式文件名带内容哈希，照常缓存。
    // 不加这条时 WebView2 会按启发式缓存直接用旧页面，更新后去找已删除的旧脚本，窗口一直空白。
    public static StaticFileOptions Options { get; } = new()
    {
        OnPrepareResponse = PrepareResponse
    };

    internal static void PrepareResponse(StaticFileResponseContext context)
    {
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
}

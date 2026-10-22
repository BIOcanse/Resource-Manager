using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

public sealed class FrontendStaticFilesTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("INDEX.HTML")]
    public void PageFilesAreRevalidatedOnEveryLoad(string fileName)
    {
        var context = Prepare(fileName);

        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("index-Dedte7_s.js")]
    [InlineData("index-BqY0Zr3a.css")]
    [InlineData("favicon.ico")]
    public void HashedAssetsKeepTheDefaultCachePolicy(string fileName)
    {
        var context = Prepare(fileName);

        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }

    private static DefaultHttpContext Prepare(string fileName)
    {
        var context = new DefaultHttpContext();
        FrontendStaticFiles.Options.OnPrepareResponse(
            new StaticFileResponseContext(context, new NotFoundFileInfo(fileName)));
        return context;
    }
}

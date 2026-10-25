using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Shell;
using ResourceManager.App.Infrastructure.Security;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Security.Principal;
using ResourceManager.App.Application.TrustedAccess;

if (OperatingSystem.IsWindows())
    WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();

WindowsShellIdentity.TrySetCurrentProcessAppUserModelId("ResourceManager.Desktop");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ResolveContentRootPath()
});
var isWindowsService = WindowsServiceHelpers.IsWindowsService();
var hostEnvironment = BackendHostEnvironment.Compile(
    isWindowsService,
    IsLocalSystem());
if (hostEnvironment.IsWindowsService)
    WindowsBackendProcessIdentityAccess.InitializeForCurrentSystemProcess();
builder.Host.UseWindowsService(options =>
    options.ServiceName = BackendHostEnvironment.WindowsServiceName);
var loopbackApiPipeline = LoopbackApiPipelineConfigurator.CompileForCurrentProcess();
var startupCapabilities = StartupProfileCompiler.Compile(args);

builder.WebHost.UseUrls("http://127.0.0.1:9321");
loopbackApiPipeline.ConfigureServices(builder.Services);
builder.Services.AddResourceManagerApp(
    args,
    startupCapabilities,
    hostEnvironment);

var app = builder.Build();
var initializeAccessKeys = args.Contains("--initialize-trusted-access", StringComparer.Ordinal);
var recoverAccessKeys = args.Contains("--recover-trusted-access", StringComparer.Ordinal);
if (initializeAccessKeys || recoverAccessKeys)
{
    if (initializeAccessKeys && recoverAccessKeys)
        throw new ArgumentException("Select exactly one trusted-access administration action.");
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("Trusted-access initialization requires an elevated administrator.");
    // Explicit administration, before the host starts any service or opens a listener.
    await app.Services.GetRequiredService<ITrustedAccessKeyService>()
        .InitializeAsync(recoverAccessKeys, CancellationToken.None);
    await app.DisposeAsync();
    return;
}
app.Logger.LogInformation(
    "Resource Manager startup profile {StartupProfile} allows {StartupCapabilities}.",
    startupCapabilities.ProfileId,
    startupCapabilities.Allowed);

loopbackApiPipeline.ConfigureApplication(app);
app.UseDefaultFiles();
app.UseStaticFiles(FrontendStaticFiles.Options);

app.MapResourceManagerEndpoints(loopbackApiPipeline.Mode, startupCapabilities);
app.Map("/api/{**unmatchedPath}", () => Results.NotFound(new ResourceManagerEndpointRouteBuilderExtensions.EndpointErrorPayload("api-endpoint-not-found")));

static string ResolveContentRootPath()
{
    var baseDirectory = AppContext.BaseDirectory;
    if (HasWebRoot(baseDirectory))
    {
        return baseDirectory;
    }

    var currentDirectory = Directory.GetCurrentDirectory();
    if (HasWebRoot(currentDirectory))
    {
        return currentDirectory;
    }

    for (var directory = new DirectoryInfo(baseDirectory);
         directory is not null;
         directory = directory.Parent)
    {
        if (HasWebRoot(directory.FullName))
        {
            return directory.FullName;
        }
    }

    return currentDirectory;
}

static bool HasWebRoot(string path)
{
    return Directory.Exists(Path.Combine(path, "wwwroot"));
}

static bool IsLocalSystem()
{
    using var identity = WindowsIdentity.GetCurrent();
    return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
}

app.MapFallbackToFile("index.html", FrontendStaticFiles.Options);

await app.RunAsync();

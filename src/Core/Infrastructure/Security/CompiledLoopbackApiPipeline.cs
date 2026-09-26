using System.Security.Principal;

namespace ResourceManager.App.Infrastructure.Security;

public enum LoopbackApiPipelineMode : byte
{
    StandardUser = 0,
    Administrator = 1
}

public sealed class CompiledLoopbackApiPipeline
{
    private readonly Action<IServiceCollection> configureServices;
    private readonly Action<WebApplication> configureApplication;

    internal CompiledLoopbackApiPipeline(
        LoopbackApiPipelineMode mode,
        Action<IServiceCollection> configureServices,
        Action<WebApplication> configureApplication)
    {
        Mode = mode;
        this.configureServices = configureServices;
        this.configureApplication = configureApplication;
    }

    public LoopbackApiPipelineMode Mode { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        configureServices(services);
    }

    public void ConfigureApplication(WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        configureApplication(application);
    }
}

public static class LoopbackApiPipelineConfigurator
{
    private static readonly CompiledLoopbackApiPipeline StandardUserPipeline = new(
        LoopbackApiPipelineMode.StandardUser,
        configureServices: ConfigureAuthenticationServices,
        configureApplication: ConfigureAuthenticationMiddleware);

    private static readonly CompiledLoopbackApiPipeline AdministratorPipeline = new(
        LoopbackApiPipelineMode.Administrator,
        configureServices: ConfigureAuthenticationServices,
        configureApplication: ConfigureAuthenticationMiddleware);

    public static CompiledLoopbackApiPipeline CompileForCurrentProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return Compile(principal.IsInRole(WindowsBuiltInRole.Administrator));
    }

    public static CompiledLoopbackApiPipeline Compile(bool processIsAdministrator) =>
        processIsAdministrator ? AdministratorPipeline : StandardUserPipeline;

    private static void ConfigureAuthenticationServices(IServiceCollection services)
    {
        services.AddSingleton<LoopbackApiAccessToken>();
        services.AddSingleton<ILoopbackApiAdministratorReader, WindowsLoopbackApiAdministratorReader>();
    }

    private static void ConfigureAuthenticationMiddleware(WebApplication application)
    {
        _ = application.Services.GetRequiredService<LoopbackApiAccessToken>();
        application.UseMiddleware<LoopbackApiAuthenticationMiddleware>();
    }
}

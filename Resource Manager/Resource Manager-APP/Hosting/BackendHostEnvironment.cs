namespace ResourceManager.App.Hosting;

public sealed record BackendHostEnvironment
{
    public const string WindowsServiceName = "ResourceManager.Service";

    private BackendHostEnvironment(bool isWindowsService)
    {
        IsWindowsService = isWindowsService;
    }

    public bool IsWindowsService { get; }

    public static BackendHostEnvironment Interactive { get; } = new(false);

    public static BackendHostEnvironment Compile(
        bool isWindowsService,
        bool isLocalSystem)
    {
        if (isWindowsService && !isLocalSystem)
        {
            throw new InvalidOperationException(
                $"{WindowsServiceName} must run as LocalSystem.");
        }

        return isWindowsService ? new(true) : Interactive;
    }
}

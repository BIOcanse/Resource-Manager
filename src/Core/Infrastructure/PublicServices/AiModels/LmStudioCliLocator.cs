namespace ResourceManager.App.Infrastructure.PublicServices.AiModels;

internal static class LmStudioCliLocator
{
    public static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("LM_STUDIO_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userInstall = Path.Combine(profile, ".lmstudio", "bin", "lms.exe");
        return File.Exists(userInstall) ? userInstall : null;
    }
}

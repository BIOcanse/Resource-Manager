using ResourceManager.App.Domain.Controlled;

namespace ResourceManager.App.Application.Controlled;

public static class ControlledRegistrationValidator
{
    public static string? Validate(ControlledSoftwareRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return "Registration command is required.";
        }

        var hasSoftware = request.Software.Any(static item => !string.IsNullOrWhiteSpace(item));
        var hasProgramRoots = request.ProgramRootPaths.Any(static item => !string.IsNullOrWhiteSpace(item));
        var hasProcesses = request.Processes.Any(static item => !string.IsNullOrWhiteSpace(item.Name) || item.ProcessId is not null || !string.IsNullOrWhiteSpace(item.ExecutablePath));
        var hasServices = request.Services.Any(static item => !string.IsNullOrWhiteSpace(item.Name));

        if (!hasSoftware && !hasProgramRoots && !hasProcesses && !hasServices)
        {
            return "At least one software, root path, process, or service target is required.";
        }

        return null;
    }
}

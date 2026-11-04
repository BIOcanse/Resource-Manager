using ResourceManager.App.Domain.Controlled;

namespace ResourceManager.App.Application.Controlled;

public sealed class InMemoryControlledSoftwareRegistry : IControlledSoftwareRegistry
{
    private readonly object gate = new();
    private readonly List<ControlledSoftwareRegistration> registrations = [];

    public IReadOnlyList<ControlledSoftwareRegistration> GetAll()
    {
        lock (gate)
        {
            return registrations
                .OrderByDescending(static registration => registration.RegisteredAt)
                .ToArray();
        }
    }

    public ControlledSoftwareRegistration Register(ControlledSoftwareRegistrationRequest request)
    {
        var registration = new ControlledSoftwareRegistration(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            request.Command.Trim(),
            CleanSoftware(request.Software),
            CleanProgramRootPaths(request.ProgramRootPaths),
            CleanProcesses(request.Processes),
            CleanServices(request.Services),
            "controlled");

        lock (gate)
        {
            registrations.Add(registration);
        }

        return registration;
    }

    public bool Remove(Guid id)
    {
        lock (gate)
        {
            var index = registrations.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            registrations.RemoveAt(index);
            return true;
        }
    }

    private static IReadOnlyList<string> CleanSoftware(IReadOnlyList<string> software)
    {
        return software
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> CleanProgramRootPaths(IReadOnlyList<string>? programRootPaths)
    {
        if (programRootPaths is null)
        {
            return [];
        }

        return programRootPaths
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => Path.GetFullPath(Environment.ExpandEnvironmentVariables(item.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ControlledProcessDeclaration> CleanProcesses(IReadOnlyList<ControlledProcessDeclaration> processes)
    {
        return processes
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name) || item.ProcessId is not null || !string.IsNullOrWhiteSpace(item.ExecutablePath))
            .Select(static item => item with
            {
                Name = item.Name.Trim(),
                ExecutablePath = string.IsNullOrWhiteSpace(item.ExecutablePath) ? null : item.ExecutablePath.Trim()
            })
            .ToArray();
    }

    private static IReadOnlyList<ControlledServiceDeclaration> CleanServices(IReadOnlyList<ControlledServiceDeclaration> services)
    {
        return services
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(static item => item with
            {
                Name = item.Name.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? null : item.DisplayName.Trim()
            })
            .ToArray();
    }
}

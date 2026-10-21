using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Application.Adaptation;

public static class AdapterRegistrationValidator
{
    public static string? Validate(AdapterSoftwareRegistrationRequest? request)
    {
        if (request is null)
        {
            return "Adapter registration request is required.";
        }

        if (!string.Equals(request.SchemaVersion, AdapterRegistrationSchemaVersions.Current, StringComparison.Ordinal))
        {
            return $"Adapter registration schemaVersion must be {AdapterRegistrationSchemaVersions.Current}.";
        }

        if (string.IsNullOrWhiteSpace(request.AdapterId))
        {
            return "adapterId is required.";
        }

        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            return "appId is required.";
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return "displayName is required.";
        }

        if (request.ProgramRootPaths is null
            || !request.ProgramRootPaths.Any(static item => !string.IsNullOrWhiteSpace(item)))
        {
            return "At least one program root path is required.";
        }

        if (request.ResourceMarkerEndpoint is null)
        {
            return "resourceMarkerEndpoint is required.";
        }

        var endpointValidation = ValidateResourceMarkerEndpoint(request.ResourceMarkerEndpoint);
        if (endpointValidation is not null)
        {
            return endpointValidation;
        }

        if (request.ProgramRootPaths.Count > 64 || request.Processes?.Count > 256)
            return "Adapter registration contains too many roots or processes.";
        return ValidateProgramPaths(request) ?? ValidateSchedulingCapabilities(request.SchedulingCapabilities);
    }

    private static string? ValidateProgramPaths(AdapterSoftwareRegistrationRequest request)
    {
        try
        {
            var roots = request.ProgramRootPaths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).ToArray();
            if (roots.Any(path => !Path.IsPathFullyQualified(path)))
                return "Program root paths must be absolute paths.";
            roots = roots.Select(Path.GetFullPath).ToArray();
            var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var sharedRoots = new[] { windowsRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            if (roots.Any(root => root.TrimEnd('\\', '/').Equals(Path.GetPathRoot(root)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(windowsRoot) && IsWithinRoot(root, windowsRoot))
                || sharedRoots.Any(shared => !string.IsNullOrWhiteSpace(shared) && root.TrimEnd('\\', '/').Equals(shared.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))))
                return "Program roots cannot be drive roots or shared system directories.";
            foreach (var process in request.Processes ?? [])
            {
                if (process.ProcessId is <= 0) return "Declared process IDs must be positive.";
                if (string.IsNullOrWhiteSpace(process.ExecutablePath)) continue;
                var executable = Environment.ExpandEnvironmentVariables(process.ExecutablePath.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(executable) || !roots.Any(root => IsWithinRoot(Path.GetFullPath(executable), root)))
                    return "Declared executable paths must be within a program root.";
            }
            return null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return "Program or executable paths are invalid."; }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static string? ValidateResourceMarkerEndpoint(AdapterResourceMarkerEndpoint endpoint)
    {
        if (!string.Equals(endpoint.Transport, AdapterResourceMarkerTransports.LoopbackHttp, StringComparison.OrdinalIgnoreCase))
        {
            return $"resourceMarkerEndpoint.transport must be {AdapterResourceMarkerTransports.LoopbackHttp}.";
        }

        if (!Uri.TryCreate(endpoint.Address, UriKind.Absolute, out var uri))
        {
            return "resourceMarkerEndpoint.address must be an absolute URI.";
        }

        if (uri.Scheme is not "http" and not "https")
        {
            return "resourceMarkerEndpoint.address must use http or https.";
        }

        if (!uri.IsLoopback)
        {
            return "resourceMarkerEndpoint.address must point to loopback.";
        }

        if (uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return "resourceMarkerEndpoint.address cannot contain credentials or a fragment.";

        return null;
    }

    private static string? ValidateSchedulingCapabilities(AdapterSoftwareSchedulingCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        if (!capabilities.HasAnyDimension)
        {
            return "schedulingCapabilities must declare cpu or gpu capabilities.";
        }

        if (capabilities.Cpu is { } cpu)
        {
            if (cpu.SupportedGrades is null || cpu.SupportedGrades.Count == 0)
            {
                return "CPU scheduling capabilities must declare at least one supported grade.";
            }

            if (cpu.SupportedGrades.Any(static grade => !Enum.IsDefined(grade)))
            {
                return "CPU scheduling capabilities contain an invalid grade.";
            }

            if (!cpu.SupportedGrades.Contains(AdapterCpuSchedulingGrade.Normal))
            {
                return "CPU scheduling capabilities must include normal.";
            }
        }

        if (capabilities.Gpu is not { } gpu)
        {
            return null;
        }

        if (gpu.SupportedGrades is null || gpu.SupportedGrades.Count == 0)
        {
            return "GPU scheduling capabilities must declare at least one supported grade.";
        }

        if (gpu.SupportedGrades.Any(static grade => !Enum.IsDefined(grade)))
        {
            return "GPU scheduling capabilities contain an invalid grade.";
        }

        return !gpu.SupportedGrades.Contains(AdapterGpuSchedulingGrade.Normal)
            ? "GPU scheduling capabilities must include normal."
            : null;
    }
}

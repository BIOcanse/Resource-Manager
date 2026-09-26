namespace ResourceManager.App.Domain.Controlled;

public sealed record ControlledSoftwareRegistrationRequest(
    string Command,
    IReadOnlyList<string> Software,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<ControlledProcessDeclaration> Processes,
    IReadOnlyList<ControlledServiceDeclaration> Services);

public sealed record ControlledProcessDeclaration(
    string Name,
    int? ProcessId,
    string? ExecutablePath);

public sealed record ControlledServiceDeclaration(
    string Name,
    string? DisplayName);

public sealed record ControlledSoftwareRegistration(
    Guid Id,
    DateTimeOffset RegisteredAt,
    string Command,
    IReadOnlyList<string> Software,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<ControlledProcessDeclaration> Processes,
    IReadOnlyList<ControlledServiceDeclaration> Services,
    string Status);

using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>A lazy factory keeps a diagnostic read from constructing or starting a business service.</summary>
public sealed record BusinessBlockRegistration(
    int Id, Func<IServiceProvider, IBusinessBlock> Create)
{
    public Type? HostedServiceType { get; init; }
}

/// <summary>An observation-only component can publish diagnostics without being a runnable block.</summary>
public sealed record BusinessBlockDiagnosticRegistration(int Id);

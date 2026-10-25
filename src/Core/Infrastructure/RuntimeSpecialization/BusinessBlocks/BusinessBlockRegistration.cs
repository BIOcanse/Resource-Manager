using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>A lazy factory keeps a diagnostic read from constructing or starting a business service.</summary>
public sealed record BusinessBlockRegistration(
    string Id, IReadOnlyList<string> Dependencies, Func<IServiceProvider, IBusinessBlock> Create)
{
    public bool ObservationOnly { get; init; }
}

// Legacy hosted StartAsync must finish before Generic Host advances to its next service.
internal interface IBusinessBlockStartup
{
    Task Started { get; }
}

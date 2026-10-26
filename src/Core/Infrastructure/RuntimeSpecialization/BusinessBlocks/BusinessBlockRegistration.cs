using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>A lazy factory keeps a diagnostic read from constructing or starting a business service.</summary>
public sealed record BusinessBlockRegistration(
    string Id, IReadOnlyList<string> Dependencies, Func<IServiceProvider, IBusinessBlock> Create)
{
    public string ParentId { get; init; } = BackendBlockGraph.Program;
    public Type? HostedServiceType { get; init; }
    public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();
}

/// <summary>A parent's rule reads only these declared facts and its immediate children's states.</summary>
public sealed record BusinessBlockDecisionRegistration(string ParentId, IReadOnlyList<string> FactKeys,
    Func<BusinessBlockDecisionInput, IReadOnlyCollection<string>> Decide);

/// <summary>An observation-only component can publish diagnostics without being a runnable block.</summary>
public sealed record BusinessBlockDiagnosticRegistration(string Id);

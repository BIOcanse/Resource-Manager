using System.Diagnostics.CodeAnalysis;
using System.Collections.Frozen;
using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Infrastructure.ExternalInvocation;

public sealed class ExternalInvocationRegistry : IExternalInvocationRegistry
{
    private readonly FrozenDictionary<string, ExternalInvocationRegistration> operations;
    private readonly ExternalInvocationRegistration[] orderedOperations;

    public ExternalInvocationRegistry(IEnumerable<IExternalInvocationModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var registrations = new Dictionary<string, ExternalInvocationRegistration>(StringComparer.Ordinal);
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in modules.OrderBy(static item => item.Descriptor.ModuleId, StringComparer.Ordinal))
        {
            ValidateModule(module);
            if (!moduleIds.Add(module.Descriptor.ModuleId))
            {
                throw new InvalidOperationException(
                    $"External invocation module '{module.Descriptor.ModuleId}' is registered more than once.");
            }

            foreach (var operation in module.Operations)
            {
                ValidateOperation(module, operation);
                var registration = new ExternalInvocationRegistration(
                    module,
                    operation);
                if (!registrations.TryAdd(operation.Descriptor.OperationId, registration))
                {
                    throw new InvalidOperationException(
                        $"External invocation operation '{operation.Descriptor.OperationId}' is registered more than once.");
                }
            }
        }

        operations = registrations.ToFrozenDictionary(StringComparer.Ordinal);
        orderedOperations = registrations.Values
            .OrderBy(static item => item.Module.ModuleId, StringComparer.Ordinal)
            .ThenBy(static item => item.Operation.Descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool TryGetOperation(
        string operationId,
        [NotNullWhen(true)]
        out ExternalInvocationRegistration? registration)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            registration = null;
            return false;
        }

        return operations.TryGetValue(operationId, out registration);
    }

    public ExternalInvocationCatalogSnapshot GetCatalog(ExternalInvocationRuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var modules = orderedOperations
            .GroupBy(static item => item.Module.ModuleId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var availability = first.Owner.Availability;
                var moduleEnabled = plan.IsModuleEnabled(first.Module);
                var catalogOperations = group
                    .Select(item => new ExternalInvocationCatalogOperation(
                        item.Operation.Descriptor,
                        moduleEnabled && plan.IsOperationEnabled(item.Operation.Descriptor),
                        availability.Available,
                        availability.Reason))
                    .ToArray();
                return new ExternalInvocationCatalogModule(
                    first.Module,
                    moduleEnabled,
                    availability,
                    catalogOperations);
            })
            .ToArray();

        return new ExternalInvocationCatalogSnapshot(
            "1.1.0",
            modules,
            DateTimeOffset.UtcNow);
    }

    private static void ValidateModule(IExternalInvocationModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!ExternalInvocationIdentifierRules.IsValidModuleId(module.Descriptor.ModuleId))
        {
            throw new InvalidOperationException(
                $"External invocation module ID '{module.Descriptor.ModuleId}' must be lowercase kebab-case.");
        }

        if (module.Operations is null)
        {
            throw new InvalidOperationException(
                $"External invocation module '{module.Descriptor.ModuleId}' has no operation collection.");
        }
    }

    private static void ValidateOperation(
        IExternalInvocationModule module,
        IExternalInvocationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var descriptor = operation.Descriptor;
        if (!ExternalInvocationIdentifierRules.IsValidOperationId(descriptor.OperationId))
        {
            throw new InvalidOperationException(
                $"External invocation operation ID '{descriptor.OperationId}' must contain at least three lowercase kebab-case segments.");
        }

        if (!string.Equals(
                descriptor.ModuleId,
                module.Descriptor.ModuleId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{descriptor.OperationId}' declares module '{descriptor.ModuleId}' but is registered by '{module.Descriptor.ModuleId}'.");
        }

        if (!ExternalInvocationIdentifierRules.IsValidContractInterfaceName(
                descriptor.ContractInterfaceName,
                descriptor.Category))
        {
            throw new InvalidOperationException(
                $"External invocation operation '{descriptor.OperationId}' contract interface '{descriptor.ContractInterfaceName}' does not match category '{descriptor.Category}'.");
        }

        if (!ExternalInvocationOperationClassificationRules.IsRiskValid(
                descriptor.Category,
                descriptor.RiskLevel))
        {
            throw new InvalidOperationException(
                $"External invocation operation '{descriptor.OperationId}' risk '{descriptor.RiskLevel}' is invalid for category '{descriptor.Category}'.");
        }
    }
}

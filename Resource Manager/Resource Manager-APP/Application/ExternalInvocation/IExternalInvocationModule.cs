using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Application.ExternalInvocation;

public interface IExternalInvocationModule
{
    ExternalInvocationModuleDescriptor Descriptor { get; }

    ExternalInvocationModuleAvailability Availability { get; }

    IReadOnlyList<IExternalInvocationOperation> Operations { get; }
}

public interface IExternalInvocationOperation
{
    ExternalInvocationOperationDescriptor Descriptor { get; }

    Type RequestType { get; }

    Type ResponseType { get; }

    bool RequestRequired { get; }

    ValueTask<object?> InvokeAsync(object? request, CancellationToken cancellationToken);
}

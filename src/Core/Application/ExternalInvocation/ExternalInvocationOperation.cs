using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Application.ExternalInvocation;

public sealed class ExternalInvocationOperation<TRequest, TResponse>(
    ExternalInvocationOperationDescriptor descriptor,
    Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
    : IExternalInvocationOperation
{
    public ExternalInvocationOperationDescriptor Descriptor { get; } = descriptor;

    public Type RequestType => typeof(TRequest);

    public Type ResponseType => typeof(TResponse);

    public bool RequestRequired => typeof(TRequest) != typeof(ExternalInvocationNoRequest);

    public async ValueTask<object?> InvokeAsync(
        object? request,
        CancellationToken cancellationToken)
    {
        TRequest typedRequest;
        if (request is TRequest value)
        {
            typedRequest = value;
        }
        else if (!RequestRequired && request is null)
        {
            typedRequest = (TRequest)(object)new ExternalInvocationNoRequest();
        }
        else
        {
            throw new ArgumentException(
                $"Operation '{Descriptor.OperationId}' requires request type '{RequestType.FullName}'.",
                nameof(request));
        }

        return await handler(typedRequest, cancellationToken);
    }
}

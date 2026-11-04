namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSoftwareIdentityException(string operation, NativeSoftwareIdentityStatus status)
    : InvalidOperationException($"Native software identity {operation} failed with {status}.")
{
    internal NativeSoftwareIdentityStatus Status { get; } = status;
}

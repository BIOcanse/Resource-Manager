using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

internal static class RuntimeProcessAttributionFailure
{
    // A missing/disposed session or incompatible ABI cannot resolve any candidate.
    // Input/frame/payload failures belong to the candidate that supplied them.
    internal static bool IsCandidateFailure(Exception error) => error is not
        (OperationCanceledException or OutOfMemoryException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        && !(error is ObjectDisposedException disposed && IsOwnerDisposed(disposed.ObjectName))
        && error is not NativeSoftwareIdentityException
        { Status: NativeSoftwareIdentityStatus.Unavailable or NativeSoftwareIdentityStatus.AbiMismatch
            or NativeSoftwareIdentityStatus.OutOfMemory };

    private static bool IsOwnerDisposed(string? name) => name is
        nameof(NativeSoftwareIdentityResolutionSession) or nameof(NativeSoftwareIdentityCatalogSession) or "SessionLease"
        || name == typeof(NativeRuntimeProcessAttributionResolver).FullName
        || name == typeof(SoftwareIdentityCatalogCompiler).FullName;

    internal static string Kind(Exception error) => error is NativeSoftwareIdentityException native
        ? $"native-{native.Status}" : error.GetType().Name;
}

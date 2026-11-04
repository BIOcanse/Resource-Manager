using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal interface IHostManagerProcessEffectValidationScopeFileCommitter
{
    void Commit(string temporaryPath, string canonicalPath, bool replaceExisting);

    void DeleteExact(string canonicalPath);
}

internal sealed class WindowsHostManagerProcessEffectValidationScopeFileCommitter
    : IHostManagerProcessEffectValidationScopeFileCommitter
{
    internal static WindowsHostManagerProcessEffectValidationScopeFileCommitter Instance { get; }
        = new();

    private WindowsHostManagerProcessEffectValidationScopeFileCommitter()
    {
    }

    public void Commit(string temporaryPath, string canonicalPath, bool replaceExisting)
    {
        if (replaceExisting)
        {
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, canonicalPath);
            return;
        }

        WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, canonicalPath);
    }

    public void DeleteExact(string canonicalPath)
        => _ = WindowsNativeAtomicFileCommitter.DeleteExact(canonicalPath);
}


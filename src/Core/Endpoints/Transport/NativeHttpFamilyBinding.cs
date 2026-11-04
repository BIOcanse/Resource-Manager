namespace ResourceManager.App.Endpoints.Transport;

// Each H1 family registers this through DI. Its native descriptor is the sole
// method/template/startup/access/binding/codec owner. H-cutover removes this
// Kestrel binding; the same native Family remains on the native connection.
internal interface INativeHttpFamilyBinding
{
    // Prepared transport capacity from the family's existing configuration.
    // Zero means this route does not consume a request body.
    int RequestBodyBytes { get; }

    // Descriptor-only lookup must not acquire a business source or start work.
    // The descriptor and all temporary context pointers end when consume returns.
    void BorrowRoute(Action<nint> consume);

    // Called only after native admission and bounded body reception. This is
    // request control/source lifetime, never a second domain current or cache.
    INativeHttpFamilyInvocation OpenInvocation();
}

internal interface INativeHttpFamilyInvocation : IDisposable
{
    // Family glue borrows its original source/callbacks synchronously. Pins and
    // GCHandles for domain fields must end before this method returns. The
    // Family Call.state owns only accepted work/cursor/completion control.
    // Native begin/step/release executes inside this same borrow. Async source
    // completion posts Call.wake; it never calls the dispatcher recursively.
    // An accepted invocation must remain borrowable through cancellation and
    // release. Work failures belong to typed progress/errors after owner drain;
    // they must not invalidate the source/control required for that drain.
    void Borrow(Action<nint> consume);
}

// Composition/test seam over the existing owned scratch provider. It never
// owns another allocation strategy, pool or retained payload.
internal interface INativeHttpFamilyWorkspaceFactory
{
    ResourceManager.App.Infrastructure.Adaptation.IComputeScratch Create(ulong bytes);
}

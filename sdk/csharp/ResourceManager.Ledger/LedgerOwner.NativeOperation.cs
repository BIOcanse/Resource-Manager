namespace ResourceManager.Ledger;

/// <summary>A synchronous native operation borrow. None of these addresses may
/// escape the callback. Native payload uses must end before owner destruction.</summary>
public readonly ref struct LedgerNativeOperation
{
    public nint Instance { get; }
    public nint Workspace { get; }
    public ulong WorkspaceBytes { get; }
    public nint Use { get; }
    public nint Push { get; }
    public nint EndUse { get; }
    public nint Close { get; }

    internal LedgerNativeOperation(nint instance, nint workspace, ulong bytes,
        nint use, nint push, nint endUse, nint close)
        => (Instance, Workspace, WorkspaceBytes, Use, Push, EndUse, Close)
            = (instance, workspace, bytes, use, push, endUse, close);
}

public delegate void LedgerNativeOperationUse(in LedgerNativeOperation operation);

public sealed unsafe partial class LedgerOwner<TUid>
{
    /// <summary>Calls a native consumer against this exact instance and the
    /// supplied dedicated operation workspace. Admission blocks destruction and
    /// workspace disposal/reuse until the synchronous callback returns. This
    /// does not create a second instance, own registrations, tick, or retain a
    /// payload lease. The registration owner must drain its native uses.</summary>
    public void BorrowNativeOperation(LedgerWorkspace workspace, LedgerNativeOperationUse consume)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(consume);
        using CallScope call = EnterCall();
        using WorkspaceUse scratch = EnterWorkspace(workspace, LedgerWorkspaceKind.Operation,
            _descriptor.OperationWorkspaceSize, _descriptor.OperationWorkspaceAlignment);
        var operation = new LedgerNativeOperation((nint)_instance, (nint)scratch.Workspace.Pointer,
            scratch.Workspace.ByteLength, (nint)_api.Use, (nint)_api.Push, (nint)_api.EndUse, (nint)_api.Close);
        consume(in operation);
    }
}

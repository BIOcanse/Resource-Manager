namespace ResourceManager.App.Application.GpuPlacement;

public enum GpuWindowActionMethod : uint { Redraw = 1, Resize = 2 }

public sealed record GpuWindowActionRequest(int ProcessId, long CreationFileTimeUtc, ulong Window, GpuWindowActionMethod Method)
{
    internal void Validate()
    {
        if (ProcessId <= 4 || CreationFileTimeUtc <= 0 || Window == 0 || !Enum.IsDefined(Method))
            throw new ArgumentException("A precise process instance, window and supported action are required.");
    }
}

internal sealed record GpuWindowState(int Left, int Top, int Width, int Height, bool Visible, bool Iconic, bool Maximized);
internal sealed record GpuWindowPreparation(GpuWindowActionRequest Request, uint WindowThreadId, GpuWindowState Before);
internal enum GpuWindowRejectionReason : uint
{
    TargetUnavailable = 1, WindowMismatch, NotEligible, StateChanged, InvalidRequest, ContextUnavailable
}
internal sealed record GpuWindowRejection(GpuWindowRejectionReason Reason, uint? NativeError);
internal enum GpuWindowCallState : uint { NotAttempted, Accepted, Rejected, TargetUnavailable }
internal sealed record GpuWindowCall(GpuWindowCallState State, uint? NativeError);
internal enum GpuWindowReadState : uint { NotRead, Available, Unavailable }
internal sealed record GpuWindowRead(GpuWindowReadState State, uint? NativeError, GpuWindowState? Value);
internal sealed record GpuWindowCompletion(GpuWindowCall Change, GpuWindowRead AfterChange, GpuWindowCall Restore, GpuWindowRead AfterRestore);
public enum GpuWindowActionOutcome { NotExecuted, RedrawRequested, WindowRestored, RestorationUnconfirmed, Unresolved }

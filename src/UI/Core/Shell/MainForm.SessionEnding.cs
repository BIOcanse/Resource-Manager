namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;

    private bool TryHandleSessionEndingMessage(ref Message message)
    {
        if (message.Msg is not (WmQueryEndSession or WmEndSession)
            || (WindowCloseRequested is null && ExitRequested is null))
            return false;

        // QUERY only grants permission. The window process does not exit on its own
        // for a session end: the resident owns program exit and stops this child
        // through the pipe (parent-wants-me-stop).
        message.Result = message.Msg == WmQueryEndSession ? new IntPtr(1) : IntPtr.Zero;

        // Form's default WM_ENDSESSION(TRUE) raises FormClosed and disposes
        // without another OnFormClosing call. Do not let it destroy a branch
        // whose frontend children are still draining. FALSE means canceled.
        return true;
    }
}

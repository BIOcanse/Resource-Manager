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

        // QUERY only grants permission. SystemEvents.SessionEnded is the single
        // confirmed-session exit entry, including when no main window exists.
        message.Result = message.Msg == WmQueryEndSession ? new IntPtr(1) : IntPtr.Zero;

        // Form's default WM_ENDSESSION(TRUE) raises FormClosed and disposes
        // without another OnFormClosing call. Do not let it destroy a branch
        // whose frontend children are still draining. FALSE means canceled.
        return true;
    }
}

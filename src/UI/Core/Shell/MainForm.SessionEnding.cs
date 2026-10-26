using Microsoft.Win32;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const long EndSessionLogoff = 0x80000000;

    private bool TryHandleSessionEndingMessage(ref Message message)
    {
        if (message.Msg is not (WmQueryEndSession or WmEndSession)
            || (WindowCloseRequested is null && ExitRequested is null))
            return false;

        // Windows must receive TRUE immediately; it owns the outer shutdown
        // deadline. The application root owns draining both trees and closing
        // this form through its window branch.
        message.Result = message.Msg == WmQueryEndSession ? new IntPtr(1) : IntPtr.Zero;
        if (message.Msg == WmQueryEndSession || message.WParam != IntPtr.Zero)
        {
            var reason = (message.LParam.ToInt64() & EndSessionLogoff) != 0
                ? SessionEndReasons.Logoff
                : SessionEndReasons.SystemShutdown;
            ExitRequested?.Invoke(this, new SessionEndingEventArgs(reason));
        }

        // Form's default WM_ENDSESSION(TRUE) raises FormClosed and disposes
        // without another OnFormClosing call. Do not let it destroy a branch
        // whose frontend children are still draining. FALSE means canceled.
        return true;
    }
}

namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private sealed class ResizeGripMessageFilter(MainForm owner) : IMessageFilter
    {
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;

        public bool PreFilterMessage(ref Message message)
        {
            if (owner.IsDisposed
                || !owner.IsHandleCreated
                || owner.WindowState != FormWindowState.Normal
                || message.Msg is not (WmMouseMove or WmLButtonDown)
                || !IsOwnedMessage(message.HWnd))
            {
                return false;
            }

            var hitTest = owner.HitTestResizeGrip(owner.PointToClient(Cursor.Position));
            if ((int)hitTest == HtClient)
            {
                return false;
            }

            Cursor.Current = CursorForResizeHit(hitTest);
            if (message.Msg == WmLButtonDown)
            {
                owner.BeginResizeMove(hitTest);
                return true;
            }

            return false;
        }

        private bool IsOwnedMessage(IntPtr handle)
        {
            return handle == owner.Handle || IsChild(owner.Handle, handle);
        }
    }
}

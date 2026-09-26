namespace ResourceManager.NativeUi;

public sealed partial class MainForm
{
    private const int ResizeGripSize = 8;
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmExitSizeMove = 0x0232;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private bool restoreMaximizedAfterCaptionDrag;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style |= WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        NativeWindowChrome.ApplyBorderlessSnapChrome(this);
        UpdateMaximizedBounds();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcCalcSize && message.WParam != IntPtr.Zero)
        {
            message.Result = IntPtr.Zero;
            return;
        }

        if (message.Msg == WmNcHitTest)
        {
            base.WndProc(ref message);
            if ((int)message.Result == HtClient)
            {
                message.Result = HitTestResizeGrip(message.LParam);
            }

            return;
        }

        base.WndProc(ref message);

        if (message.Msg == WmExitSizeMove && restoreMaximizedAfterCaptionDrag)
        {
            BeginInvoke(CompleteMaximizedCaptionDrag);
        }
    }

    private IntPtr HitTestResizeGrip(IntPtr lParam)
    {
        var cursor = PointToClient(new Point(
            unchecked((short)(long)lParam),
            unchecked((short)((long)lParam >> 16))));
        return HitTestResizeGrip(cursor);
    }

    private IntPtr HitTestResizeGrip(Point cursor)
    {
        var left = cursor.X <= ResizeGripSize;
        var right = cursor.X >= ClientSize.Width - ResizeGripSize;
        var top = cursor.Y <= ResizeGripSize;
        var bottom = cursor.Y >= ClientSize.Height - ResizeGripSize;

        var result = (top, bottom, left, right) switch
        {
            (true, _, true, _) => HtTopLeft,
            (true, _, _, true) => HtTopRight,
            (_, true, true, _) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtTop,
            (_, true, _, _) => HtBottom,
            (_, _, true, _) => HtLeft,
            (_, _, _, true) => HtRight,
            _ => HtClient
        };
        return new IntPtr(result);
    }

    private static Cursor CursorForResizeHit(IntPtr hitTest)
    {
        return (int)hitTest switch
        {
            HtLeft or HtRight => Cursors.SizeWE,
            HtTop or HtBottom => Cursors.SizeNS,
            HtTopLeft or HtBottomRight => Cursors.SizeNWSE,
            HtTopRight or HtBottomLeft => Cursors.SizeNESW,
            _ => Cursors.Default
        };
    }

    private void ToggleMaximized()
    {
        PublishWindowResizeState(resizing: true);
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            BeginInvoke(new Action(() => PublishWindowResizeState(resizing: false)));
            return;
        }

        UpdateMaximizedBounds();
        WindowState = FormWindowState.Maximized;
        BeginInvoke(new Action(() => PublishWindowResizeState(resizing: false)));
    }

    private void UpdateMaximizedBounds()
    {
        MaximizedBounds = WindowPlacement.GetWorkingAreaForControl(this);
    }

    private void BeginDragMove()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            restoreMaximizedAfterCaptionDrag = true;
            WindowPlacement.RestoreMaximizedForCaptionDrag(this, Cursor.Position);
        }
        else
        {
            restoreMaximizedAfterCaptionDrag = false;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
    }

    private void BeginResizeMove(IntPtr hitTest)
    {
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }

        PublishWindowResizeState(resizing: true);
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, hitTest.ToInt32(), 0);
    }

    private void CompleteMaximizedCaptionDrag()
    {
        if (!restoreMaximizedAfterCaptionDrag)
        {
            return;
        }

        restoreMaximizedAfterCaptionDrag = false;
        if (WindowState != FormWindowState.Normal || WindowPlacement.IsLikelyWindowsSnap(this))
        {
            return;
        }

        UpdateMaximizedBounds();
        WindowState = FormWindowState.Maximized;
    }

    protected override void OnResizeEnd(EventArgs args)
    {
        base.OnResizeEnd(args);
        PublishWindowResizeState(resizing: false);
    }

}

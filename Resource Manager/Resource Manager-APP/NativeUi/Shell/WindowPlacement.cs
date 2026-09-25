namespace ResourceManager.NativeUi;

internal static class WindowPlacement
{
    private const int MinimumVisiblePixels = 96;
    private const int MaximumCaptionOffset = 56;
    private const int SnapTolerancePixels = 14;
    private const double PreferredAspectRatio = 16d / 10d;
    private const double PreferredWorkAreaWidthRatio = 0.82d;
    private const double PreferredWorkAreaHeightRatio = 0.86d;
    private const int PreferredMaximumWidth = 1680;
    private const int PreferredMaximumHeight = 1000;

    public static Size GetPreferredPrimaryScreenSize(Size minimumSize)
    {
        var screen = Screen.PrimaryScreen
            ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0] : null);
        var workArea = screen?.WorkingArea ?? new Rectangle(Point.Empty, new Size(1280, 800));

        var maxWidth = Math.Min(PreferredMaximumWidth, (int)Math.Round(workArea.Width * PreferredWorkAreaWidthRatio));
        var maxHeight = Math.Min(PreferredMaximumHeight, (int)Math.Round(workArea.Height * PreferredWorkAreaHeightRatio));
        maxWidth = Math.Clamp(maxWidth, minimumSize.Width, workArea.Width);
        maxHeight = Math.Clamp(maxHeight, minimumSize.Height, workArea.Height);

        var width = maxWidth;
        var height = (int)Math.Round(width / PreferredAspectRatio);
        if (height > maxHeight)
        {
            height = maxHeight;
            width = (int)Math.Round(height * PreferredAspectRatio);
        }

        return new Size(
            Math.Clamp(width, minimumSize.Width, workArea.Width),
            Math.Clamp(height, minimumSize.Height, workArea.Height));
    }

    public static void PlaceForOpenOnPrimaryScreen(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.WindowState != FormWindowState.Normal)
        {
            form.WindowState = FormWindowState.Normal;
        }

        var screen = Screen.PrimaryScreen
            ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0] : Screen.FromControl(form));
        var workArea = screen.WorkingArea;
        var width = Math.Min(form.Width, workArea.Width);
        var height = Math.Min(form.Height, workArea.Height);

        if (width != form.Width || height != form.Height)
        {
            form.Size = new Size(width, height);
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(
            workArea.Left + Math.Max(0, (workArea.Width - form.Width) / 2),
            workArea.Top + Math.Max(0, (workArea.Height - form.Height) / 2));
    }

    public static bool TryPlaceForPassiveValidationOnSecondaryScreen(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var screen = Screen.AllScreens
            .Where(static candidate => !candidate.Primary)
            .OrderByDescending(static candidate => candidate.WorkingArea.Width * candidate.WorkingArea.Height)
            .FirstOrDefault();
        if (screen is null)
        {
            return false;
        }

        if (form.WindowState != FormWindowState.Normal)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = screen.WorkingArea;
        return true;
    }

    public static Rectangle GetWorkingAreaForControl(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return Screen.FromControl(form).WorkingArea;
    }

    public static void RestoreMaximizedForCaptionDrag(Form form, Point cursor)
    {
        ArgumentNullException.ThrowIfNull(form);

        var maximizedBounds = form.Bounds;
        var restoreBounds = form.RestoreBounds;
        var cursorScreen = Screen.FromPoint(cursor);
        var workArea = cursorScreen.WorkingArea;

        var restoredWidth = restoreBounds.Width > 0 ? restoreBounds.Width : form.Width;
        var restoredHeight = restoreBounds.Height > 0 ? restoreBounds.Height : form.Height;
        restoredWidth = Math.Min(Math.Max(restoredWidth, form.MinimumSize.Width), workArea.Width);
        restoredHeight = Math.Min(Math.Max(restoredHeight, form.MinimumSize.Height), workArea.Height);

        var horizontalRatio = maximizedBounds.Width > 0
            ? (cursor.X - maximizedBounds.Left) / (double)maximizedBounds.Width
            : 0.5d;
        horizontalRatio = Math.Clamp(horizontalRatio, 0.05d, 0.95d);

        var captionOffset = maximizedBounds.Height > 0
            ? cursor.Y - maximizedBounds.Top
            : 16;
        captionOffset = Math.Clamp(captionOffset, 8, Math.Min(MaximumCaptionOffset, restoredHeight - 8));

        var left = cursor.X - (int)Math.Round(restoredWidth * horizontalRatio);
        var top = cursor.Y - captionOffset;

        left = Math.Clamp(left, workArea.Left - restoredWidth + MinimumVisiblePixels, workArea.Right - MinimumVisiblePixels);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - MinimumVisiblePixels);

        form.WindowState = FormWindowState.Normal;
        form.Bounds = new Rectangle(left, top, restoredWidth, restoredHeight);
    }

    public static bool IsLikelyWindowsSnap(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var bounds = form.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var workArea = Screen.FromRectangle(bounds).WorkingArea;
        var touchesLeft = IsNear(bounds.Left, workArea.Left);
        var touchesRight = IsNear(bounds.Right, workArea.Right);
        var touchesTop = IsNear(bounds.Top, workArea.Top);
        var touchesBottom = IsNear(bounds.Bottom, workArea.Bottom);
        var fullHeight = touchesTop && touchesBottom;
        var halfWidth = IsNear(bounds.Width, workArea.Width / 2);
        var halfHeight = IsNear(bounds.Height, workArea.Height / 2);

        if (fullHeight && halfWidth && (touchesLeft || touchesRight))
        {
            return true;
        }

        return halfWidth
            && halfHeight
            && (touchesLeft || touchesRight)
            && (touchesTop || touchesBottom);
    }

    private static bool IsNear(int value, int target)
    {
        return Math.Abs(value - target) <= SnapTolerancePixels;
    }
}

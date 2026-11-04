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

        return GetPreferredPrimaryScreenSize(minimumSize, workArea);
    }

    internal static Size GetPreferredPrimaryScreenSize(Size minimumSize, Rectangle workArea)
    {
        var maximumSize = GetMaximumSize(minimumSize, workArea);
        var maxWidth = Math.Min(PreferredMaximumWidth, (int)Math.Round(workArea.Width * PreferredWorkAreaWidthRatio));
        var maxHeight = Math.Min(PreferredMaximumHeight, (int)Math.Round(workArea.Height * PreferredWorkAreaHeightRatio));
        maxWidth = Math.Clamp(maxWidth, minimumSize.Width, maximumSize.Width);
        maxHeight = Math.Clamp(maxHeight, minimumSize.Height, maximumSize.Height);

        var width = maxWidth;
        var height = (int)Math.Round(width / PreferredAspectRatio);
        if (height > maxHeight)
        {
            height = maxHeight;
            width = (int)Math.Round(height * PreferredAspectRatio);
        }

        return new Size(
            Math.Clamp(width, minimumSize.Width, maximumSize.Width),
            Math.Clamp(height, minimumSize.Height, maximumSize.Height));
    }

    public static void PlaceForOpenOnPrimaryScreen(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var screen = Screen.PrimaryScreen
            ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0] : Screen.FromControl(form));
        PlaceForOpenOnPrimaryScreen(form, screen.WorkingArea);
    }

    internal static void PlaceForOpenOnPrimaryScreen(Form form, Rectangle workArea)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.WindowState != FormWindowState.Normal)
        {
            form.WindowState = FormWindowState.Normal;
        }

        var size = ConstrainSize(form.Size, form.MinimumSize, workArea);
        if (size != form.Size)
        {
            form.Size = size;
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
        return TryPlaceForPassiveValidationOnSecondaryScreen(form, screen?.WorkingArea);
    }

    internal static bool TryPlaceForPassiveValidationOnSecondaryScreen(Form form, Rectangle? workArea)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (workArea is null)
        {
            return false;
        }

        if (form.WindowState != FormWindowState.Normal)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(workArea.Value.Location,
            ConstrainSize(workArea.Value.Size, form.MinimumSize, workArea.Value));
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

        RestoreMaximizedForCaptionDrag(form, cursor, Screen.FromPoint(cursor).WorkingArea);
    }

    internal static void RestoreMaximizedForCaptionDrag(Form form, Point cursor, Rectangle workArea)
    {
        ArgumentNullException.ThrowIfNull(form);

        var maximizedBounds = form.Bounds;
        var restoreBounds = form.RestoreBounds;

        var restoredWidth = restoreBounds.Width > 0 ? restoreBounds.Width : form.Width;
        var restoredHeight = restoreBounds.Height > 0 ? restoreBounds.Height : form.Height;
        var restoredSize = ConstrainSize(new Size(restoredWidth, restoredHeight), form.MinimumSize, workArea);
        restoredWidth = restoredSize.Width;
        restoredHeight = restoredSize.Height;

        var horizontalRatio = maximizedBounds.Width > 0
            ? (cursor.X - maximizedBounds.Left) / (double)maximizedBounds.Width
            : 0.5d;
        horizontalRatio = Math.Clamp(horizontalRatio, 0.05d, 0.95d);

        var captionOffset = maximizedBounds.Height > 0
            ? cursor.Y - maximizedBounds.Top
            : 16;
        var minimumCaptionOffset = Math.Min(8, restoredHeight);
        captionOffset = Math.Clamp(captionOffset, minimumCaptionOffset,
            Math.Max(minimumCaptionOffset, Math.Min(MaximumCaptionOffset, restoredHeight - 8)));

        var left = cursor.X - (int)Math.Round(restoredWidth * horizontalRatio);
        var top = cursor.Y - captionOffset;

        var visibleWidth = Math.Min(MinimumVisiblePixels, Math.Min(workArea.Width, restoredWidth));
        var visibleHeight = Math.Min(MinimumVisiblePixels, Math.Min(workArea.Height, restoredHeight));
        left = workArea.Width == 0 ? workArea.Left : Math.Clamp(left,
            workArea.Left - restoredWidth + visibleWidth, workArea.Right - visibleWidth);
        top = workArea.Height == 0 ? workArea.Top : Math.Clamp(top,
            workArea.Top, workArea.Bottom - visibleHeight);

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

    private static Size GetMaximumSize(Size minimumSize, Rectangle workArea)
    {
        // A display transition may report less space than the window minimum, including zero.
        return new Size(Math.Max(minimumSize.Width, workArea.Width),
            Math.Max(minimumSize.Height, workArea.Height));
    }

    private static Size ConstrainSize(Size size, Size minimumSize, Rectangle workArea)
    {
        var maximumSize = GetMaximumSize(minimumSize, workArea);
        return new Size(Math.Clamp(size.Width, minimumSize.Width, maximumSize.Width),
            Math.Clamp(size.Height, minimumSize.Height, maximumSize.Height));
    }

    private static bool IsNear(int value, int target)
    {
        return Math.Abs(value - target) <= SnapTolerancePixels;
    }
}

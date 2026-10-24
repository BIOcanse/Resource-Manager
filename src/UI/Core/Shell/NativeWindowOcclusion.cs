using System.Drawing;

namespace ResourceManager.NativeUi;

internal static class NativeWindowOcclusion
{
    internal static bool IsFullyCovered(Rectangle target, IReadOnlyCollection<Rectangle> occluders)
    {
        if (target.Width <= 0 || target.Height <= 0 || occluders.Count == 0)
            return false;

        var clipped = new List<Rectangle>(occluders.Count);
        var yCoordinates = new List<int>(2 + occluders.Count * 2) { target.Top, target.Bottom };
        foreach (var occluder in occluders)
        {
            var intersection = Rectangle.Intersect(target, occluder);
            if (intersection.Width <= 0 || intersection.Height <= 0)
                continue;
            clipped.Add(intersection);
            yCoordinates.Add(intersection.Top);
            yCoordinates.Add(intersection.Bottom);
        }
        if (clipped.Count == 0)
            return false;

        yCoordinates.Sort();
        var intervals = new List<(int Left, int Right)>(clipped.Count);
        for (var index = 0; index < yCoordinates.Count - 1; index++)
        {
            var top = yCoordinates[index];
            var bottom = yCoordinates[index + 1];
            if (top == bottom)
                continue;

            intervals.Clear();
            foreach (var rectangle in clipped)
            {
                if (rectangle.Top <= top && rectangle.Bottom >= bottom)
                    intervals.Add((rectangle.Left, rectangle.Right));
            }
            intervals.Sort(static (left, right) => left.Left.CompareTo(right.Left));

            var coveredRight = target.Left;
            foreach (var interval in intervals)
            {
                if (interval.Left > coveredRight)
                    return false;
                coveredRight = Math.Max(coveredRight, interval.Right);
                if (coveredRight >= target.Right)
                    break;
            }
            if (coveredRight < target.Right)
                return false;
        }

        return true;
    }
}

internal static class NativeUiHostVisibilityPolicy
{
    internal static bool IsVisible(bool windowVisible, bool minimized, bool fullyCovered, bool forceHidden = false) =>
        !forceHidden && windowVisible && !minimized && !fullyCovered;
}

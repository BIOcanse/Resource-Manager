using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

/// <summary>Local original Event Log transport. Query/event handles and XML
/// buffers belong to one bounded synchronous read on the creating thread.</summary>
public sealed class WindowsWheaHistoryReader
{
    public WindowsWheaHistoryCapture Read(WindowsWheaHistoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return new(false, [], false, "Windows Event Log is unavailable on this platform.");
        var timer = Stopwatch.StartNew();
        var query = EvtQuery(IntPtr.Zero, request.Channel, request.XPath, 0x1 | 0x200); // channel, reverse chronological
        if (query == IntPtr.Zero) return Failure(Marshal.GetLastWin32Error());
        try
        {
            var events = new List<WindowsWheaEvent>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var scanned = 0;
            while (scanned < request.MaximumEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = request.ReadBudget - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) return new(false, [], false, "WHEA history read budget expired.");
                var handles = new IntPtr[1];
                if (!EvtNext(query, 1, handles, (uint)Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)), 0, out var returned))
                {
                    var error = Marshal.GetLastWin32Error();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timer.Elapsed > request.ReadBudget) return new(false, [], false, "WHEA history read budget expired.");
                    if (error == 259) return new(true, events.ToArray(), false, null); // ERROR_NO_MORE_ITEMS
                    return Failure(error);
                }
                // Close even unexpected returned handles; do not keep live event objects.
                try
                {
                    if (returned != 1 || handles[0] == IntPtr.Zero) return new(false, [], false, "Event Log returned no event handle.");
                    scanned++;
                    var xml = Render(handles[0], request.MaximumRenderedEventBytes);
                    var item = xml is null ? null : WindowsWheaEventParser.Parse(xml, request);
                    if (item is not null && identities.Add(item.DeviceIdentity)) events.Add(item);
                }
                finally { foreach (var handle in handles) if (handle != IntPtr.Zero) _ = EvtClose(handle); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > request.ReadBudget) return new(false, [], false, "WHEA history read budget expired.");
            // Reaching the scan bound conservatively says coverage may be truncated.
            return new(true, events.ToArray(), true, null);
        }
        finally { _ = EvtClose(query); }
    }

    private static WindowsWheaHistoryCapture Failure(int error) => new(false, [], false, global::ResourceManager.ReadWrite.Native.Win32ErrorText.Get(error));
    private static string? Render(IntPtr handle, int maximumBytes)
    {
        if (EvtRender(IntPtr.Zero, handle, 1, 0, IntPtr.Zero, out var required, out _) || Marshal.GetLastWin32Error() != 122
            || required < 2 || required > maximumBytes || required % 2 != 0) return null;
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!EvtRender(IntPtr.Zero, handle, 1, required, buffer, out var used, out _)
                || used < 2 || used > required || used % 2 != 0 || Marshal.ReadInt16(buffer, checked((int)used - 2)) != 0) return null;
            return Marshal.PtrToStringUni(buffer, checked((int)used / 2 - 1));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(IntPtr resultSet, uint eventsSize, [Out] IntPtr[] events, uint timeout, uint flags, out uint returned);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, uint flags, uint bufferSize, IntPtr buffer, out uint bufferUsed, out uint propertyCount);
    [DllImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);
}

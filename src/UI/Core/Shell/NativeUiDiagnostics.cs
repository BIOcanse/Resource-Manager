using System.ComponentModel;
using System.Diagnostics;

namespace ResourceManager.NativeUi;

internal static class NativeUiDiagnostics
{
    internal static void OpenFolder(Action<Exception>? failed = null)
    {
        try
        {
            var path = NativeUiPaths.DiagnosticsFolder;
            Directory.CreateDirectory(path);
            var start = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception)
        {
            Trace.WriteLine(error);
            failed?.Invoke(error);
        }
    }
}

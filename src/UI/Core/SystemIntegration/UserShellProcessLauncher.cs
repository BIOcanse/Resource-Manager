using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

internal static class UserShellProcessLauncher
{
    private const int ProcessWaitSliceMilliseconds = 30 * 60 * 1000;

    internal static int? TryExecute(string[] args)
    {
        if (!args.Contains(UserShellExecuteRequest.Argument, StringComparer.Ordinal)) return null;
        if (args.Length != 2 || args[0] != UserShellExecuteRequest.Argument) return 64;
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (identity.IsSystem || process.SessionId == 0) return 5;
        try
        {
            var request = UserShellExecuteRequest.Decode(args[1]);
            var executable = ResolveExecutable(request.FileName, allowDirectory: request.Verb == "properties");
            if (request.Verb == "properties")
            {
                if (request.WaitForExit) return 2;
                return ExecuteProperties(executable) ? 0 : 2;
            }
            if (request.WaitForExit && IsHttpUri(executable)) return 2;

            var start = new ProcessStartInfo
            {
                FileName = executable, Arguments = Environment.ExpandEnvironmentVariables(request.Arguments),
                UseShellExecute = true,
                Verb = request.Verb ?? string.Empty
            };
            if (!IsHttpUri(executable)) start.WorkingDirectory = Path.GetDirectoryName(executable);
            return ExecuteShellStart(() => Process.Start(start), request.WaitForExit);
        }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException
            or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 2;
        }
    }

    internal static string ResolveExecutable(string fileName)
        => ResolveExecutable(fileName, allowDirectory: false);

    internal static string ResolveExecutable(string fileName, bool allowDirectory)
    {
        if (IsHttpUri(fileName) && Uri.TryCreate(fileName, UriKind.Absolute, out var uri))
            return uri.AbsoluteUri;

        var expanded = Environment.ExpandEnvironmentVariables(fileName);
        if (!Path.IsPathFullyQualified(expanded))
        {
            if (expanded != Path.GetFileName(expanded)) throw new InvalidDataException("Uninstaller path is relative.");
            expanded = Path.Combine(Environment.SystemDirectory, expanded);
        }
        var full = Path.GetFullPath(expanded);
        if (!File.Exists(full) && !(allowDirectory && Directory.Exists(full)))
            throw new FileNotFoundException("The requested user-shell path is missing.", full);
        return full;
    }

    private static bool IsHttpUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool ExecuteProperties(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskInvokeIdList | SeeMaskFlagNoUi,
            Verb = "properties",
            File = path,
            Show = 1
        };
        return ShellExecuteExW(ref info);
    }

    internal static int ExecuteShellStart(
        Func<Process?> startProcess,
        bool waitForExit = false,
        int waitSliceMilliseconds = ProcessWaitSliceMilliseconds,
        Action? onWaitSliceElapsed = null)
    {
        if (waitSliceMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(waitSliceMilliseconds));
        using var launched = startProcess();
        if (waitForExit)
        {
            if (launched is null) return 2;
            while (!launched.WaitForExit(waitSliceMilliseconds))
                NotifyWaitSliceElapsed(onWaitSliceElapsed);
            return launched.ExitCode;
        }

        return 0;
    }

    private static void NotifyWaitSliceElapsed(Action? observer)
    {
        if (observer is null) return;
        try
        {
            observer();
        }
        catch (Exception error)
        {
            Trace.WriteLine($"User-shell wait observer failed; retaining the wait until child exit. {error}");
        }
    }

    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const uint SeeMaskFlagNoUi = 0x00000400;

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Parent;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstanceApplication;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr KeyClass;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }
}

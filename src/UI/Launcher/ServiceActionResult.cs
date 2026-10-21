using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Localization;

namespace ResourceManager.Launcher;

internal static class ServiceActionResult
{
    internal static async Task RunElevatedAsync(string launcher, bool restart, ToolText text)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException(text.UserUnknown);
        var name = "ResourceManager.ServiceStart." + Guid.NewGuid().ToString("N");
        using var pipe = WindowsSessionPipeFactory.Create(name, $"D:P(A;;GA;;;{sid})(A;;GA;;;BA)(A;;GA;;;SY)", 4096, 4096);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var start = new ProcessStartInfo(launcher)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(launcher)!
        };
        start.ArgumentList.Add("--service-action");
        start.ArgumentList.Add("--service-result");
        start.ArgumentList.Add(name);
        if (restart) start.ArgumentList.Add("--restart");
        using var child = Process.Start(start) ?? throw new InvalidOperationException(text.LauncherServiceStartFailed);
        try
        {
            var result = ReadAsync(pipe, child.Id, timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            var error = await result.WaitAsync(timeout.Token);
            if (error.Length > 0) throw new InvalidOperationException(error);
            if (child.ExitCode != 0) throw new InvalidOperationException(text.Format(text.LauncherServiceExitFormat, child.ExitCode));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { throw new TimeoutException(text.LauncherServiceTimeout); }
    }

    private static async Task<string> ReadAsync(NamedPipeServerStream pipe, int childId, CancellationToken cancellation)
    {
        await pipe.WaitForConnectionAsync(cancellation);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != childId)
            throw new UnauthorizedAccessException("The service result came from another process.");
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, cancellation);
        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > 16_384) throw new InvalidDataException("Invalid service result length.");
        var bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, cancellation);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    internal static void Write(string name, string error)
    {
        if (!name.StartsWith("ResourceManager.ServiceStart.", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["ResourceManager.ServiceStart.".Length..], "N", out _))
            throw new ArgumentException("Invalid service result channel.");
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None,
            TokenImpersonationLevel.Identification);
        pipe.Connect(3000);
        var bytes = Encoding.UTF8.GetBytes(error);
        if (bytes.Length > 16_384) bytes = Encoding.UTF8.GetBytes(error[..Math.Min(error.Length, 4000)]);
        pipe.Write(BitConverter.GetBytes(bytes.Length));
        pipe.Write(bytes);
        pipe.Flush();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}

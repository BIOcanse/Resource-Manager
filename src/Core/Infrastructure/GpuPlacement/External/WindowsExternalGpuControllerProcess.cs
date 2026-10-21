using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

// Reuse the existing target-user/token checks. The controller retains cleanup ownership on a failed action.
internal sealed class WindowsExternalGpuControllerProcess : IDisposable
{
    private readonly NamedPipeServerStream output;
    private readonly NamedPipeServerStream errors;
    private WindowsExternalGpuControllerProcess(Process process, NamedPipeServerStream output, NamedPipeServerStream errors)
    {
        Process = process;
        this.output = output;
        this.errors = errors;
        StandardOutput = new StreamReader(output, Encoding.UTF8, false, 4096, leaveOpen: true);
        StandardError = new StreamReader(errors, Encoding.UTF8, false, 4096, leaveOpen: true);
        Ready = Task.WhenAll(ConnectAsync(output), ConnectAsync(errors));
    }
    internal Process Process { get; }
    internal TextReader StandardOutput { get; }
    internal TextReader StandardError { get; }
    internal Task Ready { get; }

    internal static WindowsExternalGpuControllerProcess Start(GpuPlacementProcessInstance target, string executable,
        IReadOnlyList<string> arguments, string directory)
    {
        if (target.ProcessStartKey > long.MaxValue) throw new ArgumentException("Invalid target creation identity.");
        using var context = WindowsGpuWindowActionLaunchContext.Open(target.ProcessId, (long)target.ProcessStartKey);
        // Only this attempt is writable by its target user. No other user's attempt is delegated.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(context.UserSid) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        var outputName = "ResourceManager.GpuController." + Guid.NewGuid().ToString("N");
        var errorName = "ResourceManager.GpuController." + Guid.NewGuid().ToString("N");
        var output = WindowsLocalPipeFactory.Create(outputName, context.PipeDacl, 4096, 4096);
        NamedPipeServerStream? errors = null;
        Process? process = null;
        var resumed = false;
        try
        {
            errors = WindowsLocalPipeFactory.Create(errorName, context.PipeDacl, 4096, 4096);
            var image = Path.GetFullPath(executable);
            using var lockedImage = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            var command = new StringBuilder();
            AppendArgument(command, image);
            foreach (var argument in arguments) AppendArgument(command, argument);
            foreach (var argument in new[] { "--resource-manager-output-pipes", outputName, errorName }) AppendArgument(command, argument);
            if (command.Length >= 32767) throw new ArgumentException("Controller command line is too long.");
            var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Desktop = @"winsta0\default" };
            context.RequireTargetAlive();
            const uint flags = 0x08000000 | 0x4 | 0x400; // no console, suspended, Unicode environment
            Native.ProcessInformation created;
            var launched = context.UserToken is { } token
                ? Native.CreateProcessAsUserW(token, image, command, IntPtr.Zero, IntPtr.Zero, false,
                    flags, context.EnvironmentBlock, directory, ref startup, out created)
                : Native.CreateProcessW(image, command, IntPtr.Zero, IntPtr.Zero, false,
                    flags, IntPtr.Zero, directory, ref startup, out created);
            if (!launched) throw Native.Error("CreateProcess(target-session GPU controller)");
            using var createdProcess = new SafeProcessHandle(created.Process, true);
            using var thread = new SafeWaitHandle(created.Thread, true);
            process = Process.GetProcessById(checked((int)created.ProcessId));
            _ = process.SafeHandle;
            context.RequireWorkerIdentity(createdProcess);
            var imageName = new StringBuilder(32768);
            var imageCharacters = (uint)imageName.Capacity;
            if (!Native.QueryFullProcessImageNameW(createdProcess, 0, imageName, ref imageCharacters))
                throw Native.Error("QueryFullProcessImageName(GPU controller)");
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(imageName.ToString()), image))
                throw new InvalidDataException("Created controller image identity changed.");
            if (Native.ResumeThread(thread) != 1) throw Native.Error("ResumeThread(GPU controller)");
            resumed = true;
            return new(process, output, errors);
        }
        catch
        {
            // Before resume the child cannot own target state. Once resumed, its normal cancel/cleanup protocol owns exit.
            if (!resumed && process is not null && !process.HasExited) process.Kill();
            process?.Dispose(); errors?.Dispose(); output.Dispose(); throw;
        }
    }

    private async Task ConnectAsync(NamedPipeServerStream pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != Process.Id)
            throw new UnauthorizedAccessException("Controller output peer is not the owned process.");
    }

    internal static void AppendArgument(StringBuilder command, string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Controller arguments cannot contain NUL.");
        if (command.Length != 0) command.Append(' ');
        command.Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            command.Append('\\', character == '"' ? checked(slashes * 2 + 1) : slashes).Append(character);
            slashes = 0;
        }
        command.Append('\\', checked(slashes * 2)).Append('"');
    }

    public void Dispose() { StandardOutput.Dispose(); StandardError.Dispose(); output.Dispose(); errors.Dispose(); Process.Dispose(); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Size;
            internal string? Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            internal ushort Show, ReservedLength;
            internal IntPtr ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string image, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string image, StringBuilder command,
            IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
        internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Helpers;

/// <summary>Owns the helper, its standard streams and its non-inherited kill-on-close Job.</summary>
internal sealed class HardwareBridgeChildProcess : IDisposable
{
    private SafeFileHandle? job;
    private readonly bool factoryStarted;
    private bool disposed;

    private HardwareBridgeChildProcess(Process process, StreamWriter input, StreamReader output,
        StreamReader error, SafeFileHandle job, bool factoryStarted)
    {
        Process = process;
        Input = input;
        Output = output;
        Error = error;
        this.job = job;
        this.factoryStarted = factoryStarted;
    }

    public Process Process { get; }
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }
    public void CloseInput() => DisposeInput(Input);

    public static HardwareBridgeChildProcess Start(ProcessStartInfo info,
        Func<ProcessStartInfo, Process?>? startFactory = null,
        Func<SafeProcessHandle, bool>? startupCleanupExitConfirmed = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (info.UseShellExecute || !info.RedirectStandardInput || !info.RedirectStandardOutput || !info.RedirectStandardError)
            throw new ArgumentException("The helper requires redirected standard streams without shell execution.", nameof(info));
        ArgumentException.ThrowIfNullOrWhiteSpace(info.FileName);

        // Null security attributes make this handle non-inheritable. It is never in HANDLE_LIST.
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = Native.Error("CreateJobObjectW");
            job.Dispose();
            throw error;
        }
        try
        {
            var limits = new Native.JobExtendedLimits { Basic = new() { LimitFlags = 0x2000 } };
            if (!Native.SetInformationJobObject(job, 9, in limits, (uint)Marshal.SizeOf<Native.JobExtendedLimits>()))
                throw Native.Error("SetInformationJobObject(KILL_ON_JOB_CLOSE)");
            return startFactory is null ? StartNative(info, job, startupCleanupExitConfirmed)
                : StartFactory(info, job, startFactory, startupCleanupExitConfirmed);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static HardwareBridgeChildProcess StartFactory(ProcessStartInfo info, SafeFileHandle job,
        Func<ProcessStartInfo, Process?> factory, Func<SafeProcessHandle, bool>? startupCleanupExitConfirmed)
    {
        // Only explicit test substitution may start before assignment. Production uses JOB_LIST below.
        var process = factory(info) ?? throw new InvalidOperationException("The helper start factory returned no process.");
        try
        {
            if (!Native.AssignProcessToJobObject(job, process.SafeHandle))
                throw Native.Error("AssignProcessToJobObject(test factory)");
            return new(process, process.StandardInput, process.StandardOutput, process.StandardError, job, true);
        }
        catch (Exception failure)
        {
            job.Dispose();
            KillFactoryProcess(process);
            // Retain the actual handle even when stream setup failed before
            // a HardwareBridgeChildProcess could be returned to its owner.
            var handle = process.SafeHandle;
            var confirmed = startupCleanupExitConfirmed?.Invoke(handle) ?? Native.WaitForSingleObject(handle, 5000) == 0;
            if (!confirmed) throw new ExitNotConfirmedException(process, failure);
            process.Dispose();
            throw;
        }
    }

    private static HardwareBridgeChildProcess StartNative(ProcessStartInfo info, SafeFileHandle job,
        Func<SafeProcessHandle, bool>? startupCleanupExitConfirmed)
    {
        SafeFileHandle? childInput = null, parentInput = null, parentOutput = null, childOutput = null,
            parentError = null, childError = null;
        StreamWriter? input = null;
        StreamReader? output = null, error = null;
        Process? process = null;
        SafeProcessHandle? nativeProcess = null;
        SafeWaitHandle? thread = null;
        var attributes = IntPtr.Zero;
        var inherited = IntPtr.Zero;
        var jobs = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var initialized = false;
        try
        {
            (childInput, parentInput) = CreatePipe(parentReads: false);
            (parentOutput, childOutput) = CreatePipe(parentReads: true);
            (parentError, childError) = CreatePipe(parentReads: true);
            nuint bytes = 0;
            if (Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes)
                || Marshal.GetLastWin32Error() != 122)
                throw Native.Error("InitializeProcThreadAttributeList(size)");
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes))
                throw Native.Error("InitializeProcThreadAttributeList");
            initialized = true;

            // Exactly the child's three pipe endpoints may cross the process boundary.
            inherited = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(inherited, 0, childInput.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size, childOutput.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 2 * IntPtr.Size, childError.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x00020002, inherited,
                    (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw Native.Error("UpdateProcThreadAttribute(HANDLE_LIST)");
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            // Assignment occurs atomically inside CreateProcessW, before the child can exist independently.
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobs,
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Native.Error("UpdateProcThreadAttribute(JOB_LIST)");

            var startup = new Native.StartupInfoEx
            {
                Startup = new()
                {
                    Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(),
                    Flags = 0x100, // STARTF_USESTDHANDLES
                    StandardInput = childInput.DangerousGetHandle(),
                    StandardOutput = childOutput.DangerousGetHandle(),
                    StandardError = childError.DangerousGetHandle()
                },
                Attributes = attributes
            };
            var command = BuildCommandLine(info);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', info.Environment
                .Where(entry => entry.Value is not null)
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Key + "=" + entry.Value)) + "\0\0");
            const uint flags = 0x00080000 | 0x00000004 | 0x00000400; // Extended startup, suspended, Unicode environment.
            if (!Native.CreateProcessW(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                    flags | (info.CreateNoWindow ? 0x08000000u : 0), environment,
                    string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory,
                    ref startup, out var created))
                throw Native.Error("CreateProcessW(helper with JOB_LIST)");
            nativeProcess = new(created.Process, true);
            thread = new(created.Thread, true);
            process = Process.GetProcessById(checked((int)created.ProcessId));
            // Acquire the wrapper's own handle while the child is suspended, before a quick exit is possible.
            _ = process.SafeHandle;
            input = new(new FileStream(parentInput, FileAccess.Write), info.StandardInputEncoding ?? new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            parentInput = null; // The stream now owns this endpoint.
            output = new(new FileStream(parentOutput, FileAccess.Read), info.StandardOutputEncoding ?? Encoding.UTF8);
            parentOutput = null;
            error = new(new FileStream(parentError, FileAccess.Read), info.StandardErrorEncoding ?? Encoding.UTF8);
            parentError = null;
            if (Native.ResumeThread(thread) == uint.MaxValue) throw Native.Error("ResumeThread(helper)");
            return new(process, input, output, error, job, false);
        }
        catch (Exception failure)
        {
            // Closing the Job also revokes a child whose managed stream/wrapper setup failed.
            job.Dispose();
            var exitConfirmed = nativeProcess is null || (startupCleanupExitConfirmed?.Invoke(nativeProcess)
                ?? Native.WaitForSingleObject(nativeProcess, 5000) == 0);
            DisposeInput(input);
            output?.Dispose();
            error?.Dispose();
            process?.Dispose();
            if (!exitConfirmed)
            {
                var retained = nativeProcess!;
                nativeProcess = null;
                throw new ExitNotConfirmedException(retained, failure);
            }
            throw;
        }
        finally
        {
            thread?.Dispose();
            nativeProcess?.Dispose();
            childInput?.Dispose();
            childOutput?.Dispose();
            childError?.Dispose();
            parentInput?.Dispose();
            parentOutput?.Dispose();
            parentError?.Dispose();
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(environment);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(bool parentReads)
    {
        var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = 1 };
        if (!Native.CreatePipe(out var read, out var write, in security, 65536))
        {
            var error = Native.Error("CreatePipe");
            read?.Dispose();
            write?.Dispose();
            throw error;
        }
        if (Native.SetHandleInformation(parentReads ? read : write, 1, 0)) return (read, write);
        var failure = Native.Error("SetHandleInformation(parent pipe)");
        read.Dispose();
        write.Dispose();
        throw failure;
    }

    private static StringBuilder BuildCommandLine(ProcessStartInfo info)
    {
        if (info.FileName.Contains('"') || info.FileName.Contains('\0'))
            throw new ArgumentException("The helper executable path contains an invalid character.", nameof(info));
        var command = new StringBuilder();
        AppendArgument(command, info.FileName);
        if (info.ArgumentList.Count != 0)
        {
            if (!string.IsNullOrEmpty(info.Arguments))
                throw new ArgumentException("Specify either Arguments or ArgumentList.", nameof(info));
            foreach (var argument in info.ArgumentList) AppendArgument(command, argument);
        }
        else if (!string.IsNullOrEmpty(info.Arguments)) command.Append(' ').Append(info.Arguments);
        if (command.Length >= 32767 || command.ToString().Contains('\0'))
            throw new ArgumentException("The helper command line is invalid or too long.", nameof(info));
        return command;
    }

    private static void AppendArgument(StringBuilder command, string argument)
    {
        if (command.Length != 0) command.Append(' ');
        command.Append('"');
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            command.Append(character);
            slashes = 0;
        }
        command.Append('\\', slashes * 2).Append('"');
    }

    /// <summary>Revokes the whole owned Job without enumerating descendants.</summary>
    public void Terminate()
    {
        Interlocked.Exchange(ref job, null)?.Dispose();
        if (factoryStarted && !disposed) KillFactoryProcess(Process);
    }

    private static void KillFactoryProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        Terminate();
        disposed = true;
        try { DisposeInput(Input); }
        finally
        {
            try { Output.Dispose(); }
            finally
            {
                try { Error.Dispose(); }
                finally { Process.Dispose(); }
            }
        }
    }

    private static void DisposeInput(StreamWriter? input)
    {
        if (input is null) return;
        // Accepted lines are explicitly flushed by the transport. Stopping must
        // close the pipe without synchronously flushing an interrupted line.
        try { input.BaseStream.Dispose(); }
        finally
        {
            try { input.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }

    internal sealed class ExitNotConfirmedException : TimeoutException, IDisposable
    {
        private readonly SafeProcessHandle handle;
        private readonly HardwareBridgeChildProcess? child;
        private readonly Process? process;
        internal ExitNotConfirmedException(SafeProcessHandle handle, Exception failure)
            : base("Job-bound helper exit is not confirmed.", failure) => this.handle = handle;
        internal ExitNotConfirmedException(HardwareBridgeChildProcess child, Exception failure)
            : this(child.Process.SafeHandle, failure) => this.child = child;
        internal ExitNotConfirmedException(Process process, Exception failure)
            : this(process.SafeHandle, failure) => this.process = process;
        internal bool HasExited
        {
            get
            {
                var result = Native.WaitForSingleObject(handle, 0);
                if (result == uint.MaxValue) throw Native.Error("WaitForSingleObject(retained helper)");
                return result == 0;
            }
        }
        public void Dispose()
        {
            if (child is not null) child.Dispose();
            else if (process is not null) process.Dispose();
            else handle.Dispose();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal uint Length;
            internal IntPtr Descriptor;
            internal int Inherit;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal uint Size;
            internal IntPtr Reserved, Desktop, Title;
            internal uint X, Y, Width, Height, CharactersX, CharactersY, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedLength;
            internal IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo Startup;
            internal IntPtr Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr Process, Thread;
            internal uint ProcessId, ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobBasicLimits
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobExtendedLimits
        {
            internal JobBasicLimits Basic;
            internal IoCounters Io;
            internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, in JobExtendedLimits value, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, in SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute,
            IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
            IntPtr environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);

        internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    }
}

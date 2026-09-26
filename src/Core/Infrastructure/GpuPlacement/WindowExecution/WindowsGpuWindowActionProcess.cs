using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

internal sealed record GpuWindowActionProcessLimits(TimeSpan TotalBudget, TimeSpan CleanupReserve, int MaximumFrameBytes, int PipeBufferBytes)
{
    internal void Validate()
    {
        if (TotalBudget <= CleanupReserve || CleanupReserve <= TimeSpan.Zero
            || TotalBudget.TotalMilliseconds > int.MaxValue || MaximumFrameBytes <= 0 || PipeBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(TotalBudget), "Positive explicit work, cleanup and framing bounds are required.");
    }
}

internal sealed record GpuWindowActionProcessIdentity(int ProcessId, long CreationFileTimeUtc, string ExecutablePath);

internal sealed record GpuWindowActionProcessCleanup(
    bool ProcessStarted, bool ExitObserved, uint? ExitCode, bool TerminationRequested,
    int? TerminationError, uint? ActiveProcessCount, bool IoCompleted, string? ObservationError)
{
    internal bool Complete => (!ProcessStarted || ExitObserved) && ActiveProcessCount == 0 && IoCompleted;
}

internal sealed partial class WindowsGpuWindowActionProcess : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly GpuWindowActionProcessLimits limits;
    private readonly ulong deadlineMilliseconds;
    private readonly SafeFileHandle job;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource workStop;
    private readonly WindowsGpuWindowActionLaunchContext? launchContext;
    private SafeProcessHandle? process;
    private SafeWaitHandle? thread;
    private FileStream? image;
    private Task? connection, read, write, exitWait;
    private Task<GpuWindowActionProcessCleanup>? stopping;
    private Task<GpuWindowActionProcessCleanup>? completion;
    private bool startAttempted, resumed, disposed, parentHandleTransferred;
    private bool terminationRequested;
    private int? terminationError;
    private string? cleanupError;

    private WindowsGpuWindowActionProcess(GpuWindowActionProcessLimits limits, SafeFileHandle job,
        NamedPipeServerStream pipe, string pipeName, CancellationTokenSource workStop, ulong deadlineMilliseconds,
        WindowsGpuWindowActionLaunchContext? launchContext)
    {
        this.limits = limits;
        this.deadlineMilliseconds = deadlineMilliseconds;
        this.job = job;
        this.pipe = pipe;
        this.workStop = workStop;
        this.launchContext = launchContext;
        PipeName = pipeName;
    }

    internal string PipeName { get; }
    internal CancellationToken WorkCancellation => workStop.Token;
    internal GpuWindowActionProcessIdentity? Identity { get; private set; }
    internal GpuWindowActionProcessCleanup? Cleanup { get; private set; }

    internal static WindowsGpuWindowActionProcess Create(GpuWindowActionProcessLimits limits, CancellationToken cancellationToken,
        ulong? deadlineMilliseconds = null)
        => CreateCore(limits, cancellationToken, deadlineMilliseconds, null);

    internal static WindowsGpuWindowActionProcess CreateForTarget(GpuWindowActionRequest request,
        GpuWindowActionProcessLimits limits, CancellationToken cancellationToken, ulong deadlineMilliseconds)
    {
        request.Validate();
        return CreateForTarget(request.ProcessId, request.CreationFileTimeUtc, limits, cancellationToken, deadlineMilliseconds);
    }

    internal static WindowsGpuWindowActionProcess CreateForTarget(int processId, long creationFileTimeUtc,
        GpuWindowActionProcessLimits limits, CancellationToken cancellationToken, ulong deadlineMilliseconds)
    {
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingWait(deadlineMilliseconds, checked((ulong)Environment.TickCount64), limits.TotalBudget) <= limits.CleanupReserve)
            throw new TimeoutException("The original window action work deadline has elapsed.");
        var context = WindowsGpuWindowActionLaunchContext.Open(processId, creationFileTimeUtc);
        try { return CreateCore(limits, cancellationToken, deadlineMilliseconds, context); }
        catch { context.Dispose(); throw; }
    }

    private static WindowsGpuWindowActionProcess CreateCore(GpuWindowActionProcessLimits limits,
        CancellationToken cancellationToken, ulong? deadlineMilliseconds, WindowsGpuWindowActionLaunchContext? launchContext)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var maximumDeadline = checked((ulong)Environment.TickCount64 + (ulong)limits.TotalBudget.TotalMilliseconds);
        var deadline = deadlineMilliseconds ?? maximumDeadline;
        if (deadline > maximumDeadline) throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { var error = Native.Error("CreateJobObject"); job.Dispose(); throw error; }
        NamedPipeServerStream? pipe = null;
        CancellationTokenSource? stop = null;
        try
        {
            var jobLimits = new Native.JobExtendedLimits
            {
                Basic = new() { LimitFlags = Native.JobLimitKillOnClose | Native.JobLimitActiveProcess, ActiveProcessLimit = 1 }
            };
            if (!Native.SetInformationJobObject(job, 9, in jobLimits, (uint)Marshal.SizeOf<Native.JobExtendedLimits>()))
                throw Native.Error("SetInformationJobObject");
            var name = "ResourceManager.GpuWindowAction." + Guid.NewGuid().ToString("N");
            pipe = launchContext is null
                ? new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, limits.PipeBufferBytes, limits.PipeBufferBytes)
                : WindowsLocalPipeFactory.Create(name, launchContext.PipeDacl, limits.PipeBufferBytes, limits.PipeBufferBytes);
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var work = RemainingWait(deadline, checked((ulong)Environment.TickCount64), limits.TotalBudget) - limits.CleanupReserve;
            if (work <= TimeSpan.Zero) stop.Cancel();
            else stop.CancelAfter(work);
            return new(limits, job, pipe, name, stop, deadline, launchContext);
        }
        catch { stop?.Dispose(); pipe?.Dispose(); job.Dispose(); throw; }
    }

    // Only the local execution owner supplies this executable/argv; they are not wire request fields.
    internal void StartSuspended(string executablePath, IReadOnlyList<string> arguments, string? desktopName = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (desktopName is not null && (string.IsNullOrWhiteSpace(desktopName) || desktopName.Contains('\0')))
            throw new ArgumentException("An explicit desktop name cannot be blank or contain NUL.", nameof(desktopName));
        var executable = Path.GetFullPath(executablePath);
        lock (sync)
        {
            CheckOpen();
            if (startAttempted) throw new InvalidOperationException("A window execution is single-use.");
            startAttempted = true;
            var errorMode = Native.GetErrorMode();
            if ((errorMode & Native.RequiredErrorMode) != Native.RequiredErrorMode)
                throw new InvalidOperationException(
                    $"Native window-helper launch policy mismatch: process={Environment.ProcessId}, " +
                    $"actual=0x{errorMode:X8}, required=0x{Native.RequiredErrorMode:X8}, " +
                    $"missing=0x{Native.RequiredErrorMode & ~errorMode:X8}.");
            var command = new StringBuilder();
            AppendArgument(command, executable);
            AppendArgument(command, PipeName);
            using var parent = Process.GetCurrentProcess();
            if (!Native.GetProcessTimes(parent.SafeHandle, out var parentCreation, out _, out _, out _))
                throw Native.Error("GetProcessTimes(parent)");
            AppendArgument(command, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendArgument(command, parentCreation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var argument in arguments) AppendArgument(command, argument);
            if (command.Length >= 32767) throw new ArgumentException("The Windows command line is too long.");
            image = new(executable, FileMode.Open, FileAccess.Read, FileShare.Read);

            nuint size = 0;
            if (Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size) || Marshal.GetLastWin32Error() != 122)
                throw Native.Error("InitializeProcThreadAttributeList(size)");
            var attributes = Marshal.AllocHGlobal(checked((nint)size));
            var jobs = IntPtr.Zero;
            var desktop = IntPtr.Zero;
            var initialized = false;
            try
            {
                jobs = Marshal.AllocHGlobal(IntPtr.Size);
                var selectedDesktop = desktopName ?? (launchContext?.UsesSessionToken == true ? "winsta0\\default" : null);
                if (selectedDesktop is not null) desktop = Marshal.StringToHGlobalUni(selectedDesktop);
                if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                    throw Native.Error("InitializeProcThreadAttributeList");
                initialized = true;
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                if (!Native.UpdateProcThreadAttribute(attributes, 0, Native.JobListAttribute, jobs,
                        (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw Native.Error("UpdateProcThreadAttribute(JOB_LIST)");
                var startup = new Native.StartupInfoEx
                {
                    StartupInfo = new() { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Desktop = desktop }, AttributeList = attributes
                };
                workStop.Token.ThrowIfCancellationRequested();
                launchContext?.RequireTargetAlive();
                var flags = Native.CreateSuspended | Native.DetachedProcess | Native.ExtendedStartupInfoPresent;
                Native.ProcessInformation created;
                var launched = launchContext?.UserToken is { } token
                    ? Native.CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        flags | Native.CreateUnicodeEnvironment, launchContext.EnvironmentBlock,
                        Path.GetDirectoryName(executable)!, ref startup, out created)
                    : Native.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        flags, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out created);
                if (!launched) throw Native.Error("CreateProcess(window worker with JOB_LIST)");
                process = new(created.Process, true);
                thread = new(created.Thread, true);
                if (!Native.GetProcessTimes(process, out var creation, out _, out _, out _))
                    throw Native.Error("GetProcessTimes(worker)");
                var imageName = new StringBuilder(32768);
                var characters = (uint)imageName.Capacity;
                if (!Native.QueryFullProcessImageNameW(process, 0, imageName, ref characters))
                    throw Native.Error("QueryFullProcessImageName");
                var identity = new GpuWindowActionProcessIdentity(checked((int)created.ProcessId), creation, Path.GetFullPath(imageName.ToString()));
                if (!identity.ExecutablePath.Equals(executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The created image is not the requested window worker.");
                if (ReadActiveProcessCount() != 1) throw new InvalidOperationException("The suspended worker is not owned by this Job.");
                launchContext?.RequireWorkerIdentity(process);
                Identity = identity;
            }
            finally
            {
                if (initialized) Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(desktop);
                Marshal.FreeHGlobal(jobs);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    internal ulong TransferParentReadHandle()
    {
        lock (sync)
        {
            CheckOpen();
            if (Identity is null || process is null || resumed || parentHandleTransferred)
                throw new InvalidOperationException("One parent read handle may be transferred to the verified suspended worker.");
            parentHandleTransferred = true;
            if (!Native.DuplicateParentHandle(Native.GetCurrentProcess(), Native.GetCurrentProcess(), process,
                    out var remoteHandle, Native.ProcessQueryAndSynchronize, false, 0))
                throw Native.Error("DuplicateHandle(parent read handle into worker)");
            // This is a handle in the child's table; only the child (or its exit) closes it.
            return checked((ulong)remoteHandle.ToInt64());
        }
    }

    internal void Resume()
    {
        lock (sync)
        {
            CheckOpen();
            if (Identity is null || thread is null || resumed) throw new InvalidOperationException("Only a verified suspended worker can be released once.");
            if (Native.ResumeThread(thread) != 1) throw Native.Error("ResumeThread");
            resumed = true;
            thread.Dispose();
            thread = null;
        }
    }

    internal Task ConnectAsync()
    {
        lock (sync)
        {
            CheckOpen();
            if (Identity is null || connection is not null) throw new InvalidOperationException("One connection per verified worker is required.");
            return connection = ConnectCoreAsync();
        }
    }

    private async Task ConnectCoreAsync()
    {
        await pipe.WaitForConnectionAsync(workStop.Token).ConfigureAwait(false);
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client))
            throw Native.Error("GetNamedPipeClientProcessId");
        if (Identity is null || client != Identity.ProcessId)
            throw new UnauthorizedAccessException("The pipe peer is not this execution's worker.");
    }

    internal Task<byte[]> ReadFrameAsync()
    {
        lock (sync)
        {
            CheckConnected();
            if (read is { IsCompleted: false }) throw new InvalidOperationException("A frame read is already in progress.");
            var operation = ReadFrameCoreAsync();
            read = operation;
            return operation;
        }
    }

    private async Task<byte[]> ReadFrameCoreAsync()
    {
        var prefix = new byte[sizeof(uint)];
        await pipe.ReadExactlyAsync(prefix, workStop.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0 || length > limits.MaximumFrameBytes) throw new InvalidDataException("Worker frame length is outside the configured bound.");
        var payload = new byte[checked((int)length)];
        await pipe.ReadExactlyAsync(payload, workStop.Token).ConfigureAwait(false);
        return payload;
    }

    internal Task WriteFrameAsync(ReadOnlyMemory<byte> payload)
    {
        lock (sync)
        {
            CheckConnected();
            if (payload.IsEmpty || payload.Length > limits.MaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(payload));
            if (write is { IsCompleted: false }) throw new InvalidOperationException("A frame write is already in progress.");
            return write = WriteFrameCoreAsync(payload);
        }
    }

    private async Task WriteFrameCoreAsync(ReadOnlyMemory<byte> payload)
    {
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
        await pipe.WriteAsync(prefix, workStop.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, workStop.Token).ConfigureAwait(false);
    }

    internal Task WaitForExitAsync()
    {
        lock (sync)
        {
            CheckOpen();
            if (process is null) throw new InvalidOperationException("No worker was started.");
            return exitWait ??= WaitForProcessAsync(process, workStop.Token);
        }
    }

    internal uint ReadActiveProcessCount()
    {
        if (!Native.QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Native.JobAccounting>(), IntPtr.Zero))
            throw Native.Error("QueryInformationJobObject");
        return accounting.ActiveProcesses;
    }

    internal Task<GpuWindowActionProcessCleanup> StopAsync()
    {
        lock (sync) { return stopping ??= StopCoreAsync(); }
    }

    private async Task<GpuWindowActionProcessCleanup> StopCoreAsync()
    {
        using var cleanupStop = new CancellationTokenSource();
        var remaining = RemainingWait(deadlineMilliseconds, checked((ulong)Environment.TickCount64), limits.CleanupReserve);
        if (remaining == TimeSpan.Zero) cleanupStop.Cancel();
        else cleanupStop.CancelAfter(remaining);
        try { workStop.Cancel(); }
        catch (AggregateException exception) { cleanupError = exception.ToString(); }
        pipe.Dispose();
        try
        {
            if (process is not null && !HasExited())
            {
                terminationRequested = true;
                if (!Native.TerminateJobObject(job, Native.AbortedExitCode)) terminationError = Marshal.GetLastWin32Error();
                try { await WaitForProcessAsync(process, cleanupStop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch (Win32Exception exception) { cleanupError = exception.ToString(); }
        var operations = new[] { connection, read, write, exitWait }.OfType<Task>().ToArray();
        var settled = Task.WhenAll(operations).ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await settled.WaitAsync(cleanupStop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return Cleanup = ObserveCleanup();
    }

    internal static TimeSpan RemainingWait(ulong deadline, ulong now, TimeSpan maximum)
        => deadline <= now ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(deadline - now, maximum.TotalMilliseconds));

    private GpuWindowActionProcessCleanup ObserveCleanup()
    {
        bool exited = false;
        uint? exitCode = null, active = null;
        string? error = cleanupError;
        try
        {
            exited = process is not null && HasExited();
            if (exited)
            {
                if (!Native.GetExitCodeProcess(process!, out var value)) throw Native.Error("GetExitCodeProcess");
                exitCode = value;
            }
            active = ReadActiveProcessCount();
        }
        catch (Win32Exception exception) { error = string.Join(Environment.NewLine, error, exception.ToString()); }
        return new(process is not null, exited, exitCode, terminationRequested, terminationError,
            active, new[] { connection, read, write, exitWait }.OfType<Task>().All(static item => item.IsCompleted), error);
    }

    internal Task<GpuWindowActionProcessCleanup> CompleteCleanupAsync()
    {
        lock (sync)
        {
            if (stopping is not { IsCompletedSuccessfully: true })
                throw new InvalidOperationException("Bounded stop must finish before handing off cleanup completion.");
            return completion ??= CompleteCleanupCoreAsync();
        }
    }

    private async Task<GpuWindowActionProcessCleanup> CompleteCleanupCoreAsync()
    {
        // Observe the same stopped execution. This does not issue another termination or window request.
        if (process is not null && !HasExited())
            await WaitForProcessAsync(process, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(new[] { connection, read, write, exitWait }.OfType<Task>()).ConfigureAwait(false);
        }
        catch { /* Original operation failures remain in the execution result. */ }
        var settled = Cleanup = ObserveCleanup();
        if (!settled.Complete)
            throw new InvalidOperationException("Window execution exit/I/O ended without confirmed native cleanup; ownership is retained.");
        return settled;
    }

    private bool HasExited()
    {
        var wait = Native.WaitForSingleObject(process!, 0);
        return wait switch { 0 => true, Native.WaitTimeout => false, _ => throw Native.Error("WaitForSingleObject") };
    }

    private static async Task WaitForProcessAsync(SafeProcessHandle process, CancellationToken cancellationToken)
    {
        if (!Native.DuplicateHandle(Native.GetCurrentProcess(), process, Native.GetCurrentProcess(), out var duplicate, 0, false, 2))
            throw Native.Error("DuplicateHandle(process wait)");
        using var wait = new ProcessWaitHandle(duplicate);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion, Timeout.Infinite, true);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try { await completion.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeWaitHandle handle) { SafeWaitHandle = handle; }
    }

    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stopping is not null) throw new InvalidOperationException("The execution is already stopping.");
        workStop.Token.ThrowIfCancellationRequested();
    }

    private void CheckConnected()
    {
        CheckOpen();
        if (connection is not { IsCompletedSuccessfully: true }) throw new InvalidOperationException("The exact worker must be connected first.");
        if (read is { IsFaulted: true } or { IsCanceled: true } || write is { IsFaulted: true } or { IsCanceled: true })
            throw new InvalidOperationException("A failed frame cannot be retried on this connection.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        var result = await StopAsync().ConfigureAwait(false);
        lock (sync)
        {
            if (disposed) return;
            if (completion is { IsCompleted: false })
                throw new InvalidOperationException("Cleanup completion still owns the native handles; retain this execution until it finishes.");
            if (!result.Complete) result = Cleanup = ObserveCleanup();
            if (!result.Complete) throw new InvalidOperationException("Window worker cleanup is unconfirmed; retain this execution object and its handles.");
            disposed = true;
            thread?.Dispose();
            process?.Dispose();
            image?.Dispose();
            job.Dispose();
            workStop.Dispose();
            launchContext?.Dispose();
        }
    }

    private static void AppendArgument(StringBuilder command, string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0')) throw new ArgumentException("Command arguments cannot contain NUL.");
        if (command.Length != 0) command.Append(' ');
        command.Append('"');
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            command.Append('\\', character == '"' ? checked(slashes * 2 + 1) : slashes);
            command.Append(character);
            slashes = 0;
        }
        command.Append('\\', checked(slashes * 2)).Append('"');
    }
}

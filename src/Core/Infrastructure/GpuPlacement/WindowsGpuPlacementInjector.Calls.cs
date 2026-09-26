using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    internal sealed class RemoteCall : GpuRemoteCallExecution
    {
        private readonly SafeKernelHandle process;
        private readonly IntPtr function;
        private readonly byte[] payload;
        private SafeKernelHandle? thread;
        private bool started;
        private bool disposed;
        private bool releaseAttempted;
        private bool responseObserved;
        private IntPtr parameter;

        internal RemoteCall(SafeKernelHandle process, GpuPlacementProcessInstance target,
            GpuRemoteCallKind kind, IntPtr function, byte[] payload, bool readResponse)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payload.Length);
            if (function == IntPtr.Zero) throw new ArgumentException("A resolved remote entry is required.");
            if (!NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), process,
                    NativeMethods.GetCurrentProcess(), out this.process, 0, false, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            this.function = function;
            this.payload = payload.ToArray();
            Request = new(Guid.NewGuid(), target, kind, checked((ulong)function.ToInt64()), payload.Length, readResponse);
        }

        public override GpuRemoteCallRequest Request { get; }
        private GpuRemoteCallSnapshot snapshot = new(0, null, null, null, null, true, "not-started", null);
        public override GpuRemoteCallSnapshot Snapshot => snapshot;

        public override void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("A remote call can only start once.");
            started = true;
            if (CheckProcessIdentity(process, Request.Process) is { } rejected)
            {
                snapshot = Snapshot with { Status = rejected.Status, NativeError = rejected.Win32Error };
                return;
            }
            parameter = NativeMethods.VirtualAllocEx(process, IntPtr.Zero, (nuint)payload.Length,
                NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
            if (parameter == IntPtr.Zero)
            {
                Fail("remote-allocation-failed");
                return;
            }
            snapshot = Snapshot with { ParameterAddress = checked((ulong)parameter.ToInt64()), ResourcesReleased = false };
            if (!NativeMethods.WriteProcessMemory(process, parameter, payload, (nuint)payload.Length, out var written)
                || written != (nuint)payload.Length)
            {
                Fail("remote-write-failed");
                ReleaseParameter();
                return;
            }
            thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, 0, function, parameter, 0, out var threadId);
            if (thread.IsInvalid)
            {
                Fail("remote-thread-failed");
                thread.Dispose();
                thread = null;
                ReleaseParameter();
                return;
            }
            snapshot = Snapshot with { ThreadId = threadId, Status = "running" };
            if (NativeMethods.GetThreadTimes(thread, out var birth, out _, out _, out _))
                snapshot = Snapshot with { ThreadCreationFileTimeUtc = birth };
            else
                Fail("remote-thread-identity-unavailable");
        }

        public override async Task WaitAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started) throw new InvalidOperationException("The action has not started this call.");
            if (thread is null || Observe().ResourcesReleased) return;
            if (!NativeMethods.DuplicateWaitHandle(NativeMethods.GetCurrentProcess(), thread,
                    NativeMethods.GetCurrentProcess(), out var duplicate, 0, false, 2))
            {
                Fail("remote-wait-handle-failed");
                return;
            }
            using var wait = new RemoteWaitHandle(duplicate);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), completion, Timeout.Infinite, true);
            using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            try { await completion.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                snapshot = Snapshot with { Status = "remote-call-cancelled" };
            }
            finally { registration.Unregister(null); }
            Observe();
        }

        public override GpuRemoteCallSnapshot Observe()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started || Snapshot.ResourcesReleased) return Snapshot;
            var processWait = NativeMethods.WaitForSingleObject(process, 0);
            if (processWait == NativeMethods.WaitObject0)
            {
                if (thread is not null && NativeMethods.WaitForSingleObject(thread, 0) == NativeMethods.WaitObject0
                    && NativeMethods.GetExitCodeThread(thread, out var exit))
                    snapshot = Snapshot with { ExitCode = exit };
                snapshot = Snapshot with { ResourcesReleased = true, Status = "target-exited" };
                return Snapshot;
            }
            if (processWait != NativeMethods.WaitTimeout) { Fail("remote-process-wait-failed"); return Snapshot; }
            if (thread is null || releaseAttempted) return Snapshot;
            var wait = NativeMethods.WaitForSingleObject(thread, 0);
            if (wait == NativeMethods.WaitTimeout) return Snapshot;
            if (wait != NativeMethods.WaitObject0) { Fail("remote-call-wait-failed"); return Snapshot; }
            if (!responseObserved)
            {
                responseObserved = true;
                if (!NativeMethods.GetExitCodeThread(thread, out var exit)) Fail("remote-status-read-failed");
                else
                {
                    snapshot = Snapshot with { ExitCode = exit, Status = "completed", NativeError = null };
                    if (Request.ReadResponse)
                    {
                        var response = new byte[payload.Length];
                        if (!NativeMethods.ReadProcessMemory(process, parameter, response, (nuint)response.Length, out var read)
                            || read != (nuint)response.Length) Fail("remote-buffer-read-failed");
                        else snapshot = Snapshot with { Response = response };
                    }
                }
            }
            ReleaseParameter();
            return Snapshot;
        }

        private void ReleaseParameter()
        {
            if (releaseAttempted) return;
            releaseAttempted = true;
            if (NativeMethods.VirtualFreeEx(process, parameter, 0, NativeMethods.MemRelease))
                snapshot = Snapshot with { ResourcesReleased = true };
            else Fail("remote-release-failed");
        }

        private void Fail(string status)
            => snapshot = Snapshot with { Status = status, NativeError = Marshal.GetLastWin32Error() };

        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;
            thread?.Dispose();
            process.Dispose();
        }

        private sealed class RemoteWaitHandle : WaitHandle
        {
            internal RemoteWaitHandle(SafeWaitHandle handle) { SafeWaitHandle = handle; }
        }
    }
}

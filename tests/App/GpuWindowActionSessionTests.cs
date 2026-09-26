using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementWindowNativeThreadTests
{
    private static GpuWindowActionRequest CurrentProcessWithNoWindow()
    {
        using var current = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(current.SafeHandle, out var born, out _, out _, out _));
        return new(current.Id, born, ulong.MaxValue, GpuWindowActionMethod.Resize);
    }

    [WindowExecutorFact]
    public void WindowSessionUsesTheHeldTargetAndExplicitLogonPipeRights()
    {
        var request = CurrentProcessWithNoWindow();
        using var context = WindowsGpuWindowActionLaunchContext.Open(request);
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.False(identity.IsSystem);
        Assert.False(context.UsesSessionToken);
        Assert.Null(context.UserToken);
        Assert.Equal(IntPtr.Zero, context.EnvironmentBlock);
        Assert.Equal(identity.User!.Value, context.UserSid);
        Assert.Equal((uint)current.SessionId, context.SessionId);
        context.RequireWorkerIdentity(current.SafeHandle);
        var descriptor = new RawSecurityDescriptor(context.PipeDacl);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Equal(2, descriptor.DiscretionaryAcl!.Count);
        var client = Assert.IsType<CommonAce>(descriptor.DiscretionaryAcl[1]);
        Assert.Equal(AceQualifier.AccessAllowed, client.AceQualifier);
        Assert.Equal(0x100003, client.AccessMask);
        Assert.StartsWith("S-1-5-5-", client.SecurityIdentifier.Value);
        output.WriteLine("windowSession=" + JsonSerializer.Serialize(new { request, context.UserSid, context.SessionId,
            context.UsesSessionToken, context.PipeDacl, serviceExecutionTested = false }));
    }

    [WindowExecutorTheory]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("disposed")]
    public async Task WindowExecutorDoesNotAllocateBeforeRunOrRenewAdmissionTime(string mode)
    {
        var request = CurrentProcessWithNoWindow();
        using var stop = new CancellationTokenSource();
        var limits = new GpuWindowActionProcessLimits(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50), 4096, 4096);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, limits, stop.Token);
        Assert.Null(executor.Worker);
        Assert.Null(executor.CleanupCompletion);
        if (mode == "expired") await Task.Delay(300);
        else if (mode == "cancelled") stop.Cancel();
        else
        {
            await executor.DisposeAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RunAsync(request, _ => Task.CompletedTask));
            Assert.Null(executor.Worker);
            return;
        }
        var result = await executor.RunAsync(request, _ => throw new Xunit.Sdk.XunitException("Admission failure reached persistence."));
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        Assert.NotNull(result.Failure);
        Assert.Null(executor.Worker);
        Assert.Null(result.Prepared);
        Assert.Null(result.Cleanup.ExitCode);
        Assert.False(result.Cleanup.ProcessStarted);
        Assert.True(result.Cleanup.Complete);
        Assert.False(result.AuthorizationMayHaveBeenSent);
        Assert.Equal(result.Cleanup, await executor.CleanupCompletion!);
        output.WriteLine("windowAdmission=" + JsonSerializer.Serialize(new { mode, request, result }));
    }

    [WindowExecutorFact]
    public async Task WindowSessionRejectsTheTargetAfterItsActualExit()
    {
        var input = InputDesktopName();
        using var window = new PrivateWindowProcess(output.WriteLine);
        window.Start();
        var request = WindowRequest(window);
        using var context = WindowsGpuWindowActionLaunchContext.Open(request);
        ClosePrivateWindow(window, input);
        Assert.Throws<InvalidDataException>(context.RequireTargetAlive);
        await using var executor = new WindowsGpuWindowActionExecutor(WindowWorkerPath, WindowLimits(), CancellationToken.None);
        var result = await executor.RunAsync(request, _ => throw new Xunit.Sdk.XunitException("An exited target reached persistence."));
        Assert.Null(executor.Worker);
        Assert.Null(result.Cleanup.ExitCode);
        Assert.False(result.Cleanup.ProcessStarted);
        Assert.True(result.Cleanup.Complete);
        Assert.Equal(GpuWindowActionOutcome.NotExecuted, result.Outcome);
        output.WriteLine("windowAdmission=" + JsonSerializer.Serialize(new { request, result, targetActuallyExited = true }));
    }

    [WindowExecutorFact]
    public async Task WindowParentTransferHasOnlyQueryAndWaitRightsAndWorksOnce()
    {
        var request = CurrentProcessWithNoWindow();
        await using var worker = WindowsGpuWindowActionProcess.CreateForTarget(request, WindowLimits(), CancellationToken.None,
            checked((ulong)Environment.TickCount64 + 5000));
        worker.StartSuspended(WindowWorkerPath, []);
        var parent = worker.TransferParentReadHandle();
        Assert.Throws<InvalidOperationException>(() => worker.TransferParentReadHandle());
        using var child = Process.GetProcessById(worker.Identity!.ProcessId);
        Assert.True(GetProcessTimes(child.SafeHandle, out var childBorn, out _, out _, out _));
        Assert.Equal(worker.Identity.CreationFileTimeUtc, childBorn);
        using var current = Process.GetCurrentProcess();
        Assert.True(DuplicateWindowTestHandle(child.SafeHandle, checked((IntPtr)(long)parent), current.SafeHandle,
            out var copy, 0, false, 2));
        using (copy)
        {
            var information = new uint[14];
            Assert.Equal(0, QueryWindowTestObject(copy, 0, information, 56, out var returned));
            Assert.Equal(56U, returned);
            Assert.Equal(0x101000U, information[1]);
            Assert.True(GetWindowTestHandleInformation(copy, out var flags));
            Assert.Equal(0U, flags & 1);
            Assert.Equal((uint)current.Id, GetWindowTestProcessId(copy));
            Assert.True(GetProcessTimes(copy, out var born, out _, out _, out _));
            Assert.Equal(request.CreationFileTimeUtc, born);
            output.WriteLine("windowParentHandle=" + JsonSerializer.Serialize(new { worker = worker.Identity,
                request, remoteHandle = parent, grantedAccess = information[1], flags, creation = born }));
        }
        worker.Resume();
        await worker.ConnectAsync();
        await worker.WriteFrameAsync(GpuWindowActionProtocol.Parent(parent));
        await worker.WriteFrameAsync(GpuWindowActionProtocol.Prepare(request));
        var rejection = GpuWindowActionProtocol.ReadRejection(await worker.ReadFrameAsync());
        Assert.Equal(GpuWindowRejectionReason.WindowMismatch, rejection.Reason);
        await worker.WaitForExitAsync();
        var cleanup = await worker.StopAsync();
        Assert.True(cleanup.Complete);
        Assert.False(cleanup.TerminationRequested);
        Assert.Equal(0U, cleanup.ExitCode);
        output.WriteLine("windowBootstrap=" + JsonSerializer.Serialize(new { worker = worker.Identity, rejection, cleanup }));
    }

    [WindowExecutorTheory]
    [InlineData("old-version", 13U)]
    [InlineData("wrong-kind", 13U)]
    [InlineData("zero-handle", 13U)]
    [InlineData("invalid-handle", 5U)]
    [InlineData("prepare-first", 13U)]
    public async Task WindowParentBootstrapRejectsInvalidFramesBeforeAnyWindowRequest(string mode, uint exit)
    {
        var request = CurrentProcessWithNoWindow();
        await using var worker = WindowsGpuWindowActionProcess.CreateForTarget(request, WindowLimits(), CancellationToken.None,
            checked((ulong)Environment.TickCount64 + 5000));
        worker.StartSuspended(WindowWorkerPath, []);
        var parent = worker.TransferParentReadHandle();
        var message = GpuWindowActionProtocol.Parent(parent);
        switch (mode)
        {
            case "old-version": BinaryPrimitives.WriteUInt32LittleEndian(message, 1); break;
            case "wrong-kind": BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), 1); break;
            case "zero-handle": BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(8), 0); break;
            case "invalid-handle": BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(8), 0x12345678); break;
            case "prepare-first": message = GpuWindowActionProtocol.Prepare(request); break;
        }
        worker.Resume();
        await worker.ConnectAsync();
        string? writeFailure = null;
        try { await worker.WriteFrameAsync(message); }
        catch (IOException exception) when (mode == "prepare-first" && (exception.HResult & 0xFFFF) is 109 or 232)
        {
            // A rejected length can close the pipe before the body write completes.
            writeFailure = exception.ToString();
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.WriteFrameAsync(message));
        }
        await worker.WaitForExitAsync();
        var cleanup = await worker.StopAsync();
        Assert.True(cleanup.Complete);
        Assert.False(cleanup.TerminationRequested);
        Assert.Equal(exit, cleanup.ExitCode);
        output.WriteLine("windowBootstrap=" + JsonSerializer.Serialize(new { mode, worker = worker.Identity, request, writeFailure, cleanup }));
    }

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateWindowTestHandle(SafeProcessHandle source, IntPtr handle, SafeProcessHandle target,
        out SafeProcessHandle copy, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryObject")]
    private static extern int QueryWindowTestObject(SafeProcessHandle handle, int kind, [Out] uint[] information, uint size, out uint returned);
    [DllImport("kernel32.dll", EntryPoint = "GetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowTestHandleInformation(SafeProcessHandle handle, out uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static extern uint GetWindowTestProcessId(SafeProcessHandle handle);
}

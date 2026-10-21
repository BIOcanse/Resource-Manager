using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection("Owned Windows kernel ETW")]
public sealed partial class KernelEtwNativeKeywordUpdateTests
{
    // Use the existing production runtime and parser while omitting the global
    // orphan reconciler: this test may only touch its own newly created session.
    private sealed class OwnedRuntimeFactory : IKernelEtwSessionRuntimeFactory
    {
        public int Created { get; private set; }
        public Action<KernelTraceEventParser>? ObserveParser { get; init; }
        public bool TryGetAvailability(out string state, out string message)
        {
            state = "Available"; message = "Owned native regression"; return true;
        }

        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        {
            Assert.Equal(ResourceManagerEtwSessionNames.KernelSessionName, name);
            Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
            var session = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = true };
            try
            {
                session.EnableKernelProvider(keywords);
                var controller = new KernelEtwSessionKeywordControl(name, new WindowsKernelEtwNativeSessionControl());
                var dispatcher = new KernelEtwSubscriptionDispatcher();
                dispatcher.AttachParser(session.Source.Kernel);
                ObserveParser?.Invoke(session.Source.Kernel);
                dispatcher.InitializeSubscriptions(subscriptions, context);
                Created++;
                return new TraceEventSessionProcessingRuntime(session, controller, dispatcher);
            }
            catch { session.Dispose(); throw; }
        }
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_ETW_TEST", "1")]
    public async Task LiveHotAdditionDeliversFileWritesAndRetainsTheOriginalProcessBinding()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(TraceEventSession.IsElevated());
        var name = ResourceManagerEtwSessionNames.Create(
            ResourceManagerEtwSessionNames.KernelPrefix, ResourceManagerEtwSessionNames.CurrentOwner);
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
        var context = new KernelEtwSessionContext(17, DateTimeOffset.UtcNow);
        var children = new ConcurrentDictionary<int, ulong>();
        var processBindings = 0;
        var processRegistration = new KernelEtwSubscriptionRegistration(1, "native-test.process",
            KernelTraceEventParser.Keywords.Process, (events, boundContext) =>
            {
                Assert.Same(context, boundContext);
                Interlocked.Increment(ref processBindings);
                events.ProcessStartGroup += data =>
                {
                    if (data.ParentID == Environment.ProcessId)
                        children[data.ProcessID] = unchecked((ulong)data.UniqueProcessKey);
                };
            });
        var directory = Path.Combine(Path.GetTempPath(), "rm-kernel-hot-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".probe");
        Directory.CreateDirectory(directory);
        try
        {
            using var session = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = true };
            session.EnableKernelProvider(processRegistration.Keywords);
            var native = new WindowsKernelEtwNativeSessionControl();
            var identity = native.Query(name);
            var dispatcher = new KernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(session.Source.Kernel);
            dispatcher.InitializeSubscriptions([processRegistration], context);
            using var runtime = new TraceEventSessionProcessingRuntime(session,
                new KernelEtwSessionKeywordControl(name, native), dispatcher);
            Exception? processingFailure = null;
            runtime.StartProcessing(error => processingFailure = error);
            try
            {
                var first = RunOwnedProcessProbe();
                await UntilAsync(() => children.ContainsKey(first), "initial process callback was not delivered");
                var written = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                KernelEtwEventSource? fileSource = null;
                var fileRegistration = new KernelEtwSubscriptionRegistration(2, "native-test.file",
                    KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit,
                    (events, boundContext) =>
                    {
                        Assert.Same(context, boundContext);
                        fileSource = events;
                        events.FileIOWrite += data =>
                        {
                            if (data.ProcessID == Environment.ProcessId && data.IoSize > 0
                                && data.FileName.EndsWith("\\" + Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))
                                written.TrySetResult(data.IoSize);
                        };
                    });
                runtime.UpdateSubscriptions([processRegistration, fileRegistration], context);
                Assert.True(fileSource!.IsActive);
                Assert.Equal(identity, native.Query(name));
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    128, FileOptions.WriteThrough))
                {
                    output.Write(new byte[128]);
                    output.Flush(flushToDisk: true);
                }
                Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(5)) >= 128);
                runtime.UpdateSubscriptions([processRegistration], context);
                Assert.False(fileSource.IsActive);
                Assert.Equal(identity, native.Query(name));
                Assert.Equal(1, processBindings);
                var second = RunOwnedProcessProbe();
                await UntilAsync(() => children.ContainsKey(second), "retained process callback was not delivered after removal");
                Assert.NotEqual(0ul, children[first]);
                Assert.NotEqual(0ul, children[second]);
                Assert.Null(processingFailure);
                Assert.True(runtime.IsProcessing);
            }
            finally
            {
                var released = runtime.Stop(TimeSpan.FromSeconds(5));
                Assert.True(released.IsReleased, string.Join("; ", released.Failures));
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory);
        }
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
    }

    private static int RunOwnedProcessProbe()
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/d /c exit 0", UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Cannot start the owned process probe.");
        Assert.True(child.WaitForExit(5000));
        Assert.Equal(0, child.ExitCode);
        return child.Id;
    }

    private static async Task UntilAsync(Func<bool> condition, string failure, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)((timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline) await Task.Delay(25);
        Assert.True(condition(), failure);
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_ETW_TEST", "1")]
    public void NativeKeywordUpdateChangesFlagsOnTheSameOwnedSession()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(TraceEventSession.IsElevated());
        var name = ResourceManagerEtwSessionNames.Create(
            ResourceManagerEtwSessionNames.KernelPrefix, ResourceManagerEtwSessionNames.CurrentOwner);
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
        using (var session = new TraceEventSession(name, TraceEventSessionOptions.Create) { StopOnDispose = true })
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            var native = new WindowsKernelEtwNativeSessionControl();
            var initial = native.Query(name);
            var control = new KernelEtwSessionKeywordControl(name, native);
            foreach (var keywords in new[]
            {
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP,
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.FileIO
                    | KernelTraceEventParser.Keywords.FileIOInit,
                KernelTraceEventParser.Keywords.Process
            })
            {
                control.UpdateKeywords(keywords);
                Assert.Equal(initial, native.Query(name));
                var flags = new uint[8];
                var status = TraceQueryInformation(initial.Handle, 4, flags, flags.Length * sizeof(uint), out var length);
                Assert.Equal(0u, status);
                Assert.Equal((uint)(flags.Length * sizeof(uint)), length);
                Assert.Equal((uint)KernelEtwSessionKeywordControl.CompileSystemTraceFlags(keywords), flags[0]);
                Assert.All(flags.Skip(1), value => Assert.Equal(0u, value));
            }
        }
        Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint TraceQueryInformation(ulong sessionHandle, uint informationClass,
        [Out] uint[] flags, int informationLength, out uint returnLength);
}

[CollectionDefinition("Owned Windows kernel ETW", DisableParallelization = true)]
public sealed class OwnedWindowsKernelEtwCollection;

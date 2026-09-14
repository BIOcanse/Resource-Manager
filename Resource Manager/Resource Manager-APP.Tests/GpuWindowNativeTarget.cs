using System.Buffers.Binary;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementWindowNativeThreadTests
{
    private sealed record TargetSize(int Width, int Height, uint Flags);
    private sealed record TargetSnapshot(int ProcessId, long CreationFileTimeUtc, ulong Window, uint ThreadId,
        long ThreadCreationFileTimeUtc, int Width, int Height, int Left, int Top, bool Visible, bool Iconic, bool Maximized,
        bool Blocked, bool TimedRelease, bool ThreadExited, bool DesktopClosed, bool WindowDestroyed,
        uint ActiveProcessCount, uint ErrorMode, uint WindowError, TargetSize[] Requests, TargetSize[] Changes);

    private sealed class PrivateWindowProcess : IDisposable
    {
        private readonly Action<string> log;
        private readonly WindowsGpuWindowActionProcess process;
        private TargetSnapshot state = null!;
        private bool stopped;
        internal PrivateWindowProcess(Action<string> log)
        {
            this.log = log;
            process = WindowsGpuWindowActionProcess.Create(new(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(5), 16384, 4096), CancellationToken.None);
            DesktopName = "ResourceManager.Gpu.WindowTest." + Guid.NewGuid().ToString("N");
            ChangingEntered = new(this);
        }

        internal string DesktopName { get; }
        internal IntPtr Handle => checked((nint)state.Window);
        internal uint NativeThreadId => state.ThreadId;
        internal long NativeThreadCreationFileTimeUtc => state.ThreadCreationFileTimeUtc;
        internal bool NativeThreadExited => state.ThreadExited;
        internal bool DesktopClosed => state.DesktopClosed;
        internal GpuWindowActionProcessIdentity Identity => process.Identity!;
        internal (int Width, int Height)[] Changes => state.Changes.Select(value => (value.Width, value.Height)).ToArray();
        internal (int Width, int Height, uint Flags)[] Requests => state.Requests.Select(value => (value.Width, value.Height, value.Flags)).ToArray();
        internal Marker ChangingEntered { get; }

        internal void Start() => StartAsync().GetAwaiter().GetResult();
        private async Task StartAsync()
        {
            process.StartSuspended(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_TARGET")!, [DesktopName]);
            process.Resume();
            await process.ConnectAsync().ConfigureAwait(false);
            state = await ReceiveAsync().ConfigureAwait(false);
            Assert.Equal(Identity.ProcessId, state.ProcessId);
            Assert.Equal(Identity.CreationFileTimeUtc, state.CreationFileTimeUtc);
            Assert.True(state.ThreadCreationFileTimeUtc > 0);
            Assert.Equal(1U, state.ActiveProcessCount);
            Assert.Equal(32771U, state.ErrorMode);
            Assert.Equal(0U, state.WindowError);
            Assert.True(state.Visible);
            log("nativeTargetReady=" + JsonSerializer.Serialize(new { worker = Identity, DesktopName, state }));
        }

        private async Task<TargetSnapshot> ReceiveAsync()
        {
            var bytes = await process.ReadFrameAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<TargetSnapshot>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("No native target response.");
        }

        private async Task SendAsync(uint command, uint value = 0)
        {
            var data = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(data, command);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), value);
            await process.WriteFrameAsync(data).ConfigureAwait(false);
            state = await ReceiveAsync().ConfigureAwait(false);
            Assert.Equal(Identity.ProcessId, state.ProcessId);
            Assert.Equal(Identity.CreationFileTimeUtc, state.CreationFileTimeUtc);
        }

        internal void Refresh() => SendAsync(1).GetAwaiter().GetResult();
        internal void BlockNextResize(uint message) => SendAsync(3, message).GetAwaiter().GetResult();
        internal void AllowResize() { if (!stopped) SendAsync(4).GetAwaiter().GetResult(); }
        internal void RefuseResizeTo(int width) => SendAsync(5, checked((uint)width)).GetAwaiter().GetResult();
        internal void CloseWindow() => SendAsync(6).GetAwaiter().GetResult();
        internal async Task<GpuWindowState> ReadAsync()
        {
            await SendAsync(1).ConfigureAwait(false);
            return new(state.Left, state.Top, state.Width, state.Height, state.Visible, state.Iconic, state.Maximized);
        }
        internal async Task<bool> ResizeAsync(int width)
        {
            await SendAsync(2, checked((uint)width)).ConfigureAwait(false);
            Assert.Equal(0U, state.WindowError);
            return true;
        }

        internal sealed class Marker(PrivateWindowProcess owner)
        {
            internal bool Wait(TimeSpan timeout)
            {
                owner.SendAsync(8, checked((uint)timeout.TotalMilliseconds)).GetAwaiter().GetResult();
                return owner.state.Blocked;
            }
        }

        public void Dispose() => StopAsync().GetAwaiter().GetResult();
        private async Task StopAsync()
        {
            if (stopped) return;
            stopped = true;
            try
            {
                await SendAsync(7).ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                var cleanup = await process.StopAsync().ConfigureAwait(false);
                log("nativeTargetCleanup=" + JsonSerializer.Serialize(new { worker = Identity, DesktopName, state, cleanup }));
                await process.DisposeAsync().ConfigureAwait(false);
                Assert.True(cleanup.Complete);
                Assert.False(cleanup.TerminationRequested);
                Assert.Equal(0U, cleanup.ExitCode);
                Assert.Null(cleanup.ObservationError);
                Assert.True(state.ThreadExited);
                Assert.True(state.DesktopClosed);
                Assert.True(state.WindowDestroyed);
                Assert.False(state.TimedRelease);
            }
        }
    }
}

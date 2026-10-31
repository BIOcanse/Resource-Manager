using System.Drawing;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.StateItems;
using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.NativeWindow.Tests;

[Collection("Native UI product composition")]
public sealed class NativeWindowApplicationTests
{
    [Fact]
    public async Task InitialUnavailableGenerationZeroIsProjectedWithoutStartingBrowser()
    {
        await using var host = await Host.StartAsync();
        await host.Receive(new("parent-wants-me-running", true));
        await host.Trees.WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Running).WaitAsync(Host.Limit);
        Assert.True(await host.HandlePublished.Task.WaitAsync(Host.Limit));
        await host.Receive(new("backend-session", false, 0));
        await host.Invoke(() =>
        {
            var projection = Field<FrontendBackendSessionProjection>(host.Window, "backendSessionProjection");
            Assert.False(projection.IsReady);
            Assert.Equal("backend-unavailable", projection.ReasonCode);
        });
        Assert.False(host.Browser.Initializing.Task.IsCompleted);
        Assert.Equal(BusinessBlockState.Stopped, host.Trees.ReadState(BlockUids.LoopbackProxy));
    }

    [Theory]
    [InlineData(false, "user-close")]
    [InlineData(true, "applyExit")]
    public async Task UserIntentWaitsForParentStop(bool applyExit, string expectedType)
    {
        await using var host = await Host.StartAsync();
        await host.Receive(new("parent-wants-me-running", true));
        await host.Trees.WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Running).WaitAsync(Host.Limit);
        await host.Invoke(() =>
        {
            if (applyExit)
                typeof(MainForm).GetMethod("HandleCommandWebMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(host.Window, ["update.applyExit"]);
            else host.Window.Close();
        });
        Assert.Equal(expectedType, await host.Intent.Task.WaitAsync(Host.Limit));
        await host.Invoke(() => Assert.False(host.Window.IsDisposed));
        Assert.False(host.Exited.IsCompleted);
        Assert.Equal(BusinessBlockState.Running, host.Trees.ReadState(BlockUids.MainWindow));
        await host.Receive(new("parent-wants-me-running", false));
        await host.Exited.WaitAsync(Host.Limit);
    }

    [Fact]
    public async Task ParentPipeLossStopsInitializingBrowserAndDrainsWholeWindow()
    {
        await using var host = await Host.StartAsync();
        await host.Receive(new("parent-wants-me-running", true));
        await host.Trees.WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Running).WaitAsync(Host.Limit);
        await host.Receive(new("backend-session", true, 1));
        await host.Browser.Initializing.Task.WaitAsync(Host.Limit);
        host.ParentWrite.Dispose();
        await host.Exited.WaitAsync(Host.Limit);
        Assert.True(host.Browser.Stopped);
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Host : IAsyncDisposable
    {
        internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
        private readonly TaskCompletionSource ready = Signal();
        private readonly TaskCompletionSource exited = Signal();
        private readonly Thread thread;
        private Control dispatcher = null!;
        private NativeWindowApplicationContext context = null!;
        internal readonly AnonymousPipeServerStream ParentWrite = new(PipeDirection.Out, HandleInheritability.None);
        internal readonly Browser Browser = new();
        internal readonly TaskCompletionSource<string> Intent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> HandlePublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal NativeWindowBlockTrees Trees => Field<NativeWindowBlockTrees>(context, "trees");
        internal MainForm Window => Field<MainForm>(context, "form");
        internal Task Exited => exited.Task;
        private Host() { thread = new(Run) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); }
        internal static async Task<Host> StartAsync()
        {
            var host = new Host(); host.thread.Start();
            await host.ready.Task.WaitAsync(Limit);
            return host;
        }
        private void Run()
        {
            try
            {
                using var sync = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(sync);
                using var ui = new Control(); _ = ui.Handle; dispatcher = ui;
                using var input = new AnonymousPipeClientStream(PipeDirection.In, ParentWrite.ClientSafePipeHandle);
                using var output = new IntentStream(Intent, () => HandlePublished.TrySetResult(
                    Window is { IsDisposed: false, Visible: true }
                    && Trees.ReadState(BlockUids.MainWindow) == BusinessBlockState.Running));
                using var pipe = new ResidentWindowPipe(input, output, 10);
                var services = new NativeUiApplicationServices
                {
                    ApiBaseAddress = "http://127.0.0.1:19321/",
                    // An isolated cache: the window process's overlay-settings migration reads it on the first ready session.
                    FrontendPreferencesPath = Path.Combine(Path.GetTempPath(), "rm-window-test-" + Guid.NewGuid().ToString("N"),
                        "frontend-preferences.cache.json"),
                    CreateFrontendHost = _ => Browser,
                    CreateSession = () => new("http://127.0.0.1:19321/", () => string.Empty,
                        () => new BackendProcessIdentity(Environment.ProcessId, 1, "isolated-fixture.exe")),
                    PresentWindow = (window, _, _) =>
                    {
                        window.ShowInTaskbar = false; window.Location = new Point(-30000, -30000); window.ShowPassively();
                    }
                };
                using var application = new NativeWindowApplicationContext(pipe, services); context = application;
                ready.SetResult(); Application.Run(application); exited.TrySetResult();
            }
            catch (Exception error) { ready.TrySetException(error); exited.TrySetException(error); }
        }
        internal Task Receive(ResidentWindowMessage message) => Invoke(async () =>
            await (Task)typeof(NativeWindowApplicationContext).GetMethod("ReceiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(context, [message])!);
        internal Task Invoke(Action action) => Invoke(() => { action(); return Task.CompletedTask; });
        private Task Invoke(Func<Task> action)
        {
            var done = Signal();
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
            }));
            return done.Task.WaitAsync(Limit);
        }
        public async ValueTask DisposeAsync()
        {
            ParentWrite.Dispose();
            await exited.Task.WaitAsync(Limit);
            Assert.True(thread.Join(Limit), "Window message loop did not finish.");
        }
    }
    private sealed class IntentStream(TaskCompletionSource<string> intent, Action handlePublished) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            var type = json.RootElement.GetProperty("type").GetString();
            if (type is "user-close" or "applyExit") intent.TrySetResult(type);
            if (type == "window-handle" && json.RootElement.GetProperty("handle").GetString() != "0") handlePublished();
            base.Write(buffer);
        }
    }
    private sealed class Browser : IFrontendHost
    {
        private readonly TaskCompletionSource initialized = Signal();
        internal TaskCompletionSource Initializing { get; } = Signal();
        internal bool Stopped { get; private set; }
        public Control View { get; } = new Panel();
        public Color BackgroundColor => Color.Black;
        public bool IsReady => false;
        public string ApplicationAddress => "http://127.0.0.1/";
        public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public Task InitializeAsync() { Initializing.TrySetResult(); return initialized.Task; }
        public Task StopAsync() { Stopped = true; initialized.TrySetCanceled(); View.Dispose(); return Task.CompletedTask; }
        public void Dispose() => View.Dispose();
        public void Navigate(string address) { }
        public void PostStringMessage(string message) { }
        public void PostJsonMessage(string json) { }
        public void FocusContent() { }
    }
}

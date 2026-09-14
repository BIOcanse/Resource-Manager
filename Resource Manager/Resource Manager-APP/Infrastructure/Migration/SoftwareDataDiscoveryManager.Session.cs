using System.Collections.Concurrent;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Infrastructure.Migration.Etw;
namespace ResourceManager.App.Infrastructure.Migration;
public sealed partial class SoftwareDataDiscoveryManager
{
    private sealed partial class DiscoverySessionState
    {
        private readonly ConcurrentDictionary<string, CandidateState> candidates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, ProcessState> observedProcesses = new();
        private readonly ConcurrentDictionary<int, byte> attributedProcessIds = new();
        private readonly ConcurrentDictionary<int, byte> activeAttributedProcessIds = new();
        private readonly List<FileSystemWatcher> watchers = [];
        private readonly IReadOnlyList<DiscoveryRoot> roots;
        private readonly IFirstRunFileTrace trace;
        private readonly CancellationTokenSource cancellation = new();
        private readonly object gate = new();
        private int observedWriteCount;
        private bool sawProcess;

        public DiscoverySessionState(
            string id,
            string softwareName,
            IReadOnlyList<string> processNames,
            IReadOnlyList<string> programRootPaths,
            IReadOnlyList<SoftwareRootResolutionSource> programRootSources,
            IReadOnlyList<DiscoveryRoot> roots,
            IFirstRunFileTrace trace)
        {
            Id = id;
            SoftwareName = softwareName;
            ProcessNames = processNames;
            ProgramRootPaths = programRootPaths;
            ProgramRootSources = programRootSources;
            this.roots = roots;
            this.trace = trace;
            StartedAt = DateTimeOffset.Now;
        }

        public string Id { get; }

        public string SoftwareName { get; }

        public IReadOnlyList<string> ProcessNames { get; }

        public IReadOnlyList<string> ProgramRootPaths { get; }

        public IReadOnlyList<SoftwareRootResolutionSource> ProgramRootSources { get; }

        public string State { get; private set; } = "Running";

        public DateTimeOffset StartedAt { get; }

        public DateTimeOffset? StoppedAt { get; private set; }

        public int ObservedWriteCount => observedWriteCount;

        public string Message { get; private set; } = "正在监控首次运行窗口内的 C 盘数据写入。";

        public string Provider { get; private set; } = "WindowsETW";

        public string ProviderState { get; private set; } = "Starting";

        public string ProviderMessage { get; private set; } = "正在启动 Windows ETW。";

        public void Start()
        {
            SeedExistingProcesses();
            var traceResult = trace.Start(
                new FirstRunTraceOptions(Id, roots.Select(static root => root.Path).ToArray()),
                ObserveTraceEvent);
            Provider = traceResult.Provider;
            ProviderState = traceResult.State;
            ProviderMessage = traceResult.Message;
            Message = traceResult.Message;

            if (!traceResult.Started)
            {
                Provider = "DirectoryWatcher";
                ProviderState = "Fallback";
                StartDirectoryWatchers();
            }

            _ = Task.Run(ProcessMonitorLoopAsync);
        }

        public void Stop(string message)
        {
            lock (gate)
            {
                if (State != "Running")
                {
                    return;
                }

                State = "Stopped";
                StoppedAt = DateTimeOffset.Now;
                Message = message;
                if (ProviderState == "Running")
                {
                    ProviderState = "Stopped";
                }
            }

            cancellation.Cancel();
            trace.Stop();
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            watchers.Clear();
        }

        public SoftwareDataDiscoverySession ToDto()
        {
            return new SoftwareDataDiscoverySession(
                Id,
                SoftwareName,
                ProcessNames,
                ProgramRootPaths,
                ProgramRootSources,
                State,
                StartedAt,
                StoppedAt,
                ObservedWriteCount,
                candidates.Values.Select(static item => item.ToDto()).OrderByDescending(static item => item.ObservedWriteCount).ToArray(),
                Message,
                Provider,
                ProviderState,
                ProviderMessage);
        }
    }
}
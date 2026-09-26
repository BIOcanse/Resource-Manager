using System.Diagnostics;
using ResourceManager.App.Infrastructure.Migration.Etw;
namespace ResourceManager.App.Infrastructure.Migration;
public sealed partial class SoftwareDataDiscoveryManager
{
    private sealed partial class DiscoverySessionState
    {
        private void SeedExistingProcesses()
        {
            if (ProcessNames.Count == 0 && ProgramRootPaths.Count == 0)
            {
                return;
            }

            foreach (var processName in ProcessNames)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        observedProcesses[process.Id] = new ProcessState(process.Id, NormalizeProcessName(process.ProcessName), null, null, true);
                        MarkAttributedProcess(process.Id);
                    }
                }
                catch
                {
                }
            }

            if (ProgramRootPaths.Count == 0)
            {
                return;
            }

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var imagePath = process.MainModule?.FileName;
                    if (!IsUnderAnyProgramRoot(imagePath))
                    {
                        continue;
                    }

                    observedProcesses[process.Id] = new ProcessState(process.Id, NormalizeProcessName(process.ProcessName), imagePath, null, true);
                    MarkAttributedProcess(process.Id);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private async Task ProcessMonitorLoopAsync()
        {
            if (ProcessNames.Count == 0 && ProgramRootPaths.Count == 0)
            {
                return;
            }

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                while (await timer.WaitForNextTickAsync(cancellation.Token))
                {
                    var running = IsAnyAttributedProcessRunning();
                    sawProcess |= running;
                    if (sawProcess && !running)
                    {
                        Stop("目标进程已退出，首次运行监控结束。");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private bool IsAnyAttributedProcessRunning()
        {
            try
            {
                var runningProcesses = Process.GetProcesses();
                try
                {
                    var runningIds = runningProcesses.Select(static process => process.Id).ToHashSet();
                    foreach (var processId in activeAttributedProcessIds.Keys)
                    {
                        if (runningIds.Contains(processId))
                        {
                            return true;
                        }

                        activeAttributedProcessIds.TryRemove(processId, out _);
                    }

                    if (ProcessNames.Count == 0)
                    {
                        return false;
                    }

                    var runningNames = runningProcesses.Select(static process => process.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return ProcessNames.Any(runningNames.Contains);
                }
                finally
                {
                    foreach (var process in runningProcesses)
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private void ObserveTraceEvent(FileTraceEventRecord record)
        {
            if (State != "Running")
            {
                return;
            }

            if (record.EventType is "ProcessStart" or "ProcessStop")
            {
                ObserveProcessEvent(record);
                return;
            }

            var path = WindowsPathMapper.ToDosPath(record.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if ((ProcessNames.Count > 0 || ProgramRootPaths.Count > 0) && !IsAttributedProcess(record.ProcessId, record.ProcessName))
            {
                return;
            }

            if (!ShouldCreateMigrationCandidate(record.EventType, path))
            {
                return;
            }

            ObservePath(record.Provider, record.EventType, path, record.ProcessId, record.ProcessName, record.SizeBytes, record.ObservedAt);
        }

        private static bool ShouldCreateMigrationCandidate(string eventType, string path)
        {
            if (eventType is "FileWrite" or "FileRename" or "FileDelete" or "FileDeleteName")
            {
                return true;
            }

            if (eventType is "FileCreate" or "FileCreateName")
            {
                return Directory.Exists(path);
            }

            return false;
        }

        private void ObserveProcessEvent(FileTraceEventRecord record)
        {
            var processName = NormalizeProcessName(record.ProcessName);
            var imagePath = WindowsPathMapper.ToDosPath(record.Path);
            var isStop = record.EventType == "ProcessStop";
            var isTarget = ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
                || IsUnderAnyProgramRoot(imagePath)
                || (record.ParentProcessId is int parentId && attributedProcessIds.ContainsKey(parentId));

            observedProcesses[record.ProcessId] = new ProcessState(record.ProcessId, processName, imagePath, record.ParentProcessId, isTarget);
            if (isTarget && !isStop)
            {
                MarkAttributedProcess(record.ProcessId);
            }
            else if (isStop)
            {
                activeAttributedProcessIds.TryRemove(record.ProcessId, out _);
            }
        }

        private bool IsAttributedProcess(int processId, string processName)
        {
            if (attributedProcessIds.ContainsKey(processId))
            {
                return true;
            }

            var normalizedName = NormalizeProcessName(processName);
            if (ProcessNames.Contains(normalizedName, StringComparer.OrdinalIgnoreCase))
            {
                MarkAttributedProcess(processId);
                observedProcesses[processId] = new ProcessState(processId, normalizedName, null, null, true);
                return true;
            }

            if (IsLiveProcessUnderProgramRoot(processId, normalizedName))
            {
                return true;
            }

            if (observedProcesses.TryGetValue(processId, out var processState) && processState.IsAttributed)
            {
                MarkAttributedProcess(processId);
                return true;
            }

            return false;
        }

        private void MarkAttributedProcess(int processId)
        {
            attributedProcessIds[processId] = 0;
            activeAttributedProcessIds[processId] = 0;
            sawProcess = true;
        }

        private bool IsLiveProcessUnderProgramRoot(int processId, string processName)
        {
            if (ProgramRootPaths.Count == 0)
            {
                return false;
            }

            if (observedProcesses.TryGetValue(processId, out var processState)
                && IsUnderAnyProgramRoot(processState.ImagePath))
            {
                MarkAttributedProcess(processId);
                return true;
            }

            var imagePath = TryGetProcessImagePath(processId);
            if (!IsUnderAnyProgramRoot(imagePath))
            {
                return false;
            }

            observedProcesses[processId] = new ProcessState(processId, processName, imagePath, null, true);
            MarkAttributedProcess(processId);
            return true;
        }

        private static string? TryGetProcessImagePath(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private bool IsUnderAnyProgramRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || ProgramRootPaths.Count == 0)
            {
                return false;
            }

            return ProgramRootPaths.Any(root => IsUnder(path, root));
        }
    }
}
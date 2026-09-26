using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Migration;
namespace ResourceManager.App.Infrastructure.Migration;
public sealed partial class SoftwareDataDiscoveryManager
{
    private sealed record DiscoveryRoot(string Path, string TargetCategory);
    private sealed record ProcessState(
        int ProcessId,
        string ProcessName,
        string? ImagePath,
        int? ParentProcessId,
        bool IsAttributed);

    private sealed class CandidateState(string path, string rootPath, string targetCategory, bool hasProcessFilter)
    {
        private const int MaxEvidenceItems = 12;
        private readonly ConcurrentDictionary<string, byte> processNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<SoftwareDataDiscoveryEvidence> evidenceItems = new();
        private int observedWriteCount;

        public string Path { get; } = path;

        public string RootPath { get; } = rootPath;

        public string TargetCategory { get; } = targetCategory;

        public DateTimeOffset LastObservedAt { get; private set; } = DateTimeOffset.Now;

        public int ObservedWriteCount => observedWriteCount;

        public void Touch(
            string provider,
            string eventType,
            string changedPath,
            int processId,
            string processName,
            long? sizeBytes,
            DateTimeOffset observedAt)
        {
            Interlocked.Increment(ref observedWriteCount);
            LastObservedAt = observedAt;

            if (!string.IsNullOrWhiteSpace(processName) && processName != "unknown")
            {
                processNames[processName] = 0;
            }

            evidenceItems.Enqueue(new SoftwareDataDiscoveryEvidence(
                provider,
                eventType,
                changedPath,
                processId,
                processName,
                observedAt,
                sizeBytes));

            while (evidenceItems.Count > MaxEvidenceItems && evidenceItems.TryDequeue(out _))
            {
            }
        }

        public SoftwareDataDiscoveryCandidate ToDto()
        {
            var evidence = evidenceItems.ToArray();
            var provider = evidence.LastOrDefault()?.Provider ?? "Unknown";
            var confidence = provider == "WindowsETW"
                ? hasProcessFilter ? 0.95 : 0.75
                : hasProcessFilter ? 0.65 : 0.5;
            var message = provider == "WindowsETW"
                ? "ETW 首次运行窗口内归因到目标进程或其子进程的文件写入。"
                : "降级目录窗口内观察到写入；这是候选证据，不是精确进程归因。";

            return new SoftwareDataDiscoveryCandidate(
                Path,
                RootPath,
                provider == "WindowsETW" ? "EtwFirstRunTrace" : "FirstRunWriteWindow",
                TargetCategory,
                "Data",
                confidence,
                ObservedWriteCount,
                LastObservedAt,
                message,
                provider,
                evidence.OrderByDescending(static item => item.ObservedAt).ToArray(),
                processNames.Keys.OrderBy(static item => item).ToArray());
        }
    }

    private static class WindowsPathMapper
    {
        private static readonly Lazy<IReadOnlyList<(string DevicePath, string Drive)>> DeviceMappings = new(BuildDeviceMappings);

        public static string? ToDosPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Trim();
            if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }

            if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && IsDirectorySeparator(normalized[2]))
            {
                return normalized;
            }

            foreach (var (devicePath, drive) in DeviceMappings.Value)
            {
                if (normalized.Equals(devicePath, StringComparison.OrdinalIgnoreCase))
                {
                    return drive + Path.DirectorySeparatorChar;
                }

                if (normalized.StartsWith(devicePath + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return drive + normalized[devicePath.Length..];
                }
            }

            return normalized;
        }

        private static IReadOnlyList<(string DevicePath, string Drive)> BuildDeviceMappings()
        {
            if (!OperatingSystem.IsWindows())
            {
                return [];
            }

            var mappings = new List<(string DevicePath, string Drive)>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                var driveName = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(driveName))
                {
                    continue;
                }

                var buffer = new StringBuilder(512);
                if (QueryDosDevice(driveName, buffer, buffer.Capacity) == 0)
                {
                    continue;
                }

                var devicePath = buffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(devicePath))
                {
                    mappings.Add((devicePath, driveName));
                }
            }

            return mappings
                .OrderByDescending(static item => item.DevicePath.Length)
                .ToArray();
        }

        private static bool IsDirectorySeparator(char value)
        {
            return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
    }
    private static string NormalizeProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "unknown";
        }

        return Path.GetFileNameWithoutExtension(processName.Trim());
    }
}
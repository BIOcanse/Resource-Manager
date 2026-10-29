using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryMeasurement;

internal sealed class Ownership
{
    static readonly HashSet<string> RuntimeNames = new(StringComparer.OrdinalIgnoreCase)
        { "coreclr", "clr", "clrjit", "hostfxr", "hostpolicy", "mscorlib", "netstandard", "mscordaccore", "dotnet" };
    readonly HashSet<string> own = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> evidence = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> pathEvidence = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> OwnModuleNames => own;
    public IReadOnlyDictionary<string, string> Evidence => evidence;
    internal static string Name(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/')).Split(',')[0];
        if (name.EndsWith(".ni.dll", StringComparison.OrdinalIgnoreCase)) return name[..^7];
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return name[..^4];
        return name;
    }
    internal Ownership(string root, IEnumerable<string> extra)
    {
        foreach (var name in extra) own.Add(Name(name));
        var pending = new Stack<string>();
        foreach (string path in new[] { Path.Combine(root, "src"), Path.Combine(root, "scripts", "dev") })
            if (Directory.Exists(path)) pending.Push(path);
        int nodes = 0; var clock = Stopwatch.StartNew();
        while (pending.TryPop(out string? directory))
        {
            if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("Repo output-name inventory exceeded 30 seconds.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++nodes > 100000) throw new InvalidDataException("Repo output-name inventory exceeded 100000 entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(entry) is not ("bin" or "obj" or "node_modules" or "dist" or "wwwroot" or ".zig-cache" or ".local")) pending.Push(entry);
                    continue;
                }
                if (entry.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
                {
                    own.Add(Path.GetFileNameWithoutExtension(entry));
                    foreach (Match match in Regex.Matches(File.ReadAllText(entry), @"<(?:AssemblyName|TargetName)>([^<$]+)</(?:AssemblyName|TargetName)>")) own.Add(Name(match.Groups[1].Value));
                }
                else if (Path.GetFileName(entry) == "build.zig")
                {
                    // Artifact declarations only; imported external module names are not ours.
                    foreach (Match match in Regex.Matches(File.ReadAllText(entry), "b\\.add(?:Library|SharedLibrary|Executable)\\s*\\(\\s*\\.\\{\\s*\\.name\\s*=\\s*\"([^\"]+)\"")) own.Add(Name(match.Groups[1].Value));
                }
            }
        }
    }
    internal void Observe(string path)
    {
        if (pathEvidence.ContainsKey(path)) return;
        string name = Name(path);
        if (name.Length == 0) return;
        string part;
        if (path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase) && evidence.TryGetValue(name, out string? alias)) part = alias;
        else if (RuntimeNames.Contains(name) || path.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\shared\\Microsoft.NETCore.App\\", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\shared\\Microsoft.WindowsDesktop.App\\", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\shared\\Microsoft.AspNetCore.App\\", StringComparison.OrdinalIgnoreCase)) part = "runtime-windows";
        else part = "external";
        if (!RuntimeNames.Contains(name) && Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\", StringComparison.Ordinal) && File.Exists(path))
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                part = ProductPart(part, version.ProductName, version.CompanyName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        pathEvidence[path] = part;
        if (evidence.TryGetValue(name, out string? previous) && previous != part) evidence[name] = "unresolved";
        else evidence[name] = part;
    }
    internal static bool IsPlatformProduct(string? product, string? company) =>
        string.Equals(company, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
        (product is ".NET" or "Microsoft\u00ae .NET" or "Microsoft .NET" or "Microsoft .NET Runtime" ||
            (product?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true && product.Contains("Operating System", StringComparison.OrdinalIgnoreCase)));
    internal static string ProductPart(string fallback, string? product, string? company)
    {
        // A non-Microsoft DLL remains external even when installed under Windows/System32.
        if (!string.IsNullOrWhiteSpace(company) && !company.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return "external";
        return IsPlatformProduct(product, company) ? "runtime-windows" : fallback;
    }
    internal string Classify(string path)
    {
        string name = Name(path);
        if (name.Equals("unresolved", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(name)) return "unresolved";
        if (name.StartsWith("ResourceManager", StringComparison.OrdinalIgnoreCase) || own.Contains(name)) return "own";
        if (pathEvidence.TryGetValue(path, out var knownPath)) return knownPath;
        if (evidence.TryGetValue(name, out var part)) return part;
        if (name.Equals("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || RuntimeNames.Contains(name)) return "runtime-windows";
        return "external";
    }
}

internal sealed class ModuleMemory
{
    public string Module { get; set; } = "";
    public string Path { get; set; } = "";
    public string Part { get; set; } = "";
    public ulong ImageCommittedBytes { get; set; }
    public ulong MappedCommittedBytes { get; set; }
    public ulong ImageResidentBytes { get; set; }
    public ulong ImagePrivateResidentBytes { get; set; }
    public ulong WorkingSetQueryFailedBytes { get; set; }
    public ulong? PrivateDirtyBytes => null;
}
internal sealed class ThreadDelta
{
    public int ThreadId { get; set; }
    public string Status { get; set; } = "";
    public string StartAddress { get; set; } = "";
    public string Module { get; set; } = "unresolved";
    public string Part { get; set; } = "unresolved";
    public double? UserMilliseconds { get; set; }
    public double? KernelMilliseconds { get; set; }
    public double? WindowSeconds { get; set; }
    public double? PercentOfOneCore => WindowSeconds > 0 ? (UserMilliseconds + KernelMilliseconds) / (WindowSeconds * 10) : null;
}
internal sealed class FootprintReport
{
    public int ProcessId { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string Attribution => "own: ResourceManager prefix or repo build output inventory; runtime-windows: Windows/shared .NET framework paths, runtime host names, and Microsoft .NET/Windows product version metadata (including app-bundled runtime DLLs); external: other loaded modules/files (license not inferred). File-backed mappings are reported separately from MEM_IMAGE; image and managed heap are not summed into total process ownership. Native private allocations are unresolved, never charged to a DLL based on its image size.";
    public string WorkingSetSemantics => "QueryWorkingSetEx Valid=1 and Shared=0 measures private resident image bytes, a copy-on-write indicator. There is no dirty bit in this API; PrivateDirtyBytes is null. Nonresident pages are not faulted in for this measurement.";
    public string ThreadSemantics => "Thread start module identifies the thread entry point, not necessarily its current CPU owner. User/kernel times are actual GetThreadTimes deltas. Threads are enumerated at window endpoints; threads born and exited entirely between endpoints are unobserved. New threads and inaccessible starts are explicit; exited original threads retain their handle and final CPU times.";
    public string CpuSource { get; set; } = "";
    public CpuReport? Cpu { get; set; }
    public List<ModuleMemory> Modules { get; set; } = new();
    public List<ThreadDelta> Threads { get; set; } = new();
    public ManagedReport Managed { get; set; } = null!;
    public List<MemoryClass> CommittedClasses { get; set; } = new();
    public List<object> Parts { get; set; } = new();
    public IReadOnlyCollection<string> RepoOutputNames { get; set; } = Array.Empty<string>();
    public List<string> Notes { get; set; } = new();
    public double ElapsedSeconds { get; set; }
    public long ToolPeakWorkingSetBytes { get; set; }
    public long ToolPeakCommittedBytes { get; set; }
}
internal static class Footprint
{
    internal static FootprintReport Run(int pid, string? speedscope, int seconds, string root, string? clrMd, string[] extra)
    {
        var clock = Stopwatch.StartNew(); var budget = new Budget(seconds);
        using var handle = Native.Open(pid); using var target = Process.GetProcessById(pid);
        var report = new FootprintReport { ProcessId = pid, CpuSource = speedscope ?? "none (no speedscope input supplied)" };
        var ownership = new Ownership(root, extra); report.RepoOutputNames = ownership.OwnModuleNames;
        var images = new List<(ulong Start, ulong End, string Path)>();
        try
        {
            foreach (ProcessModule module in target.Modules)
            {
                if (images.Count >= 16384) throw new InvalidOperationException("16384 module bound exceeded.");
                images.Add(((ulong)module.BaseAddress, (ulong)module.BaseAddress + (ulong)module.ModuleMemorySize, module.FileName));
                ownership.Observe(module.FileName);
            }
        }
        catch (System.ComponentModel.Win32Exception error) { report.Notes.Add("Native module list unavailable: " + error.Message); }

        report.Threads = Threads(target, seconds, images, ownership);
        report.Managed = ManagedHeap.Read(pid, true, clrMd, ownership.Classify);
        // Loaded managed modules often use MEM_MAPPED rather than MEM_IMAGE.
        foreach (var module in report.Managed.LoadedModules) ownership.Observe(module.Name);
        // Classify managed aggregates again after all module paths have been observed.
        report.Managed.Reclassify(ownership.Classify);
        var regions = AddressSpace.Read(handle, report.Managed.Ranges, budget);
        var modules = new Dictionary<string, ModuleMemory>(StringComparer.OrdinalIgnoreCase);
        ModuleMemory GetModule(string path)
        {
            string key = path.Length == 0 ? "unresolved" : path;
            if (!modules.TryGetValue(key, out var module))
            {
                module = new ModuleMemory { Module = Ownership.Name(key), Path = key, Part = ownership.Classify(key) };
                modules.Add(key, module);
            }
            return module;
        }
        foreach (var image in images) GetModule(image.Path);
        foreach (var managed in report.Managed.LoadedModules) GetModule(managed.Name);
        var ws = new Native.Ws[4096];
        foreach (var region in regions)
        {
            budget.Check();
            if (region.Class == "mapped")
            {
                ownership.Observe(region.Module);
                GetModule(region.Module).MappedCommittedBytes += region.Bytes;
                continue;
            }
            if (region.Class != "image") continue;
            string path = images.FirstOrDefault(m => m.Start <= region.Address && region.Address < m.End).Path ?? region.Module;
            ownership.Observe(path); var module = GetModule(path); module.ImageCommittedBytes += region.Bytes;
            ulong offset = 0;
            while (offset < region.Bytes)
            {
                budget.Check(); int pages = (int)Math.Min((ulong)ws.Length, (region.Bytes - offset + 4095) / 4096);
                int i = 0; while (i < pages) { ws[i] = new Native.Ws { Address = region.Address + offset + (ulong)i * 4096 }; i++; }
                if (!Native.QueryWorkingSetEx(handle, ws, pages * 16)) module.WorkingSetQueryFailedBytes += Math.Min((ulong)pages * 4096, region.Bytes - offset);
                else
                {
                    i = 0; while (i < pages)
                    {
                        if ((ws[i].Flags & 1) != 0)
                        {
                            ulong bytes = Math.Min(4096UL, region.Bytes - offset - (ulong)i * 4096);
                            module.ImageResidentBytes += bytes;
                            if ((ws[i].Flags & (1UL << 15)) == 0) module.ImagePrivateResidentBytes += bytes;
                        }
                        i++;
                    }
                }
                offset += (ulong)pages * 4096;
            }
        }
        foreach (var m in modules.Values) m.Part = ownership.Classify(m.Path);
        report.Modules = modules.Values.OrderByDescending(m => m.ImageCommittedBytes).ThenBy(m => m.Module).ToList();
        report.CommittedClasses = regions.GroupBy(r => r.Class).Select(g => new MemoryClass { Class = g.Key, CommittedBytes = g.Aggregate(0UL, (s, r) => s + r.Bytes) }).ToList();
        if (speedscope != null) report.Cpu = CpuSamples.Read(speedscope, ownership.Classify);
        foreach (var part in new[] { "own", "external", "runtime-windows", "unresolved" })
        {
            var moduleRows = report.Modules.Where(m => m.Part == part).ToArray();
            var heap = report.Managed.Assemblies.Where(m => m.Part == part).ToArray();
            report.Parts.Add(new { Part = part, ImageCommittedBytes = moduleRows.Aggregate(0UL, (s, m) => s + m.ImageCommittedBytes),
                MappedCommittedBytes = moduleRows.Aggregate(0UL, (s, m) => s + m.MappedCommittedBytes),
                ImagePrivateResidentBytes = moduleRows.Aggregate(0UL, (s, m) => s + m.ImagePrivateResidentBytes),
                ManagedObjectBytes = report.Managed.Complete ? (ulong?)heap.Aggregate(0UL, (s, m) => s + m.Bytes) : null });
        }
        report.ElapsedSeconds = clock.Elapsed.TotalSeconds; using var self = Process.GetCurrentProcess(); self.Refresh();
        report.ToolPeakWorkingSetBytes = self.PeakWorkingSet64; report.ToolPeakCommittedBytes = self.PeakPagedMemorySize64;
        return report;
    }

    static List<ThreadDelta> Threads(Process target, int seconds, List<(ulong Start, ulong End, string Path)> images, Ownership ownership)
    {
        var records = new List<ThreadDelta>();
        var held = new List<(IntPtr Handle, long Created, long User, long Kernel, long Timestamp, ThreadDelta Row)>();
        try
        {
            target.Refresh();
            foreach (ProcessThread thread in target.Threads)
            {
                if (records.Count >= 8192) throw new InvalidOperationException("8192 thread bound exceeded.");
                var row = new ThreadDelta { ThreadId = thread.Id }; records.Add(row);
                IntPtr handle = Native.OpenThread(0x40 | 0x800, false, thread.Id);
                if (handle == IntPtr.Zero) { row.Status = "open failed: " + Marshal.GetLastWin32Error(); continue; }
                bool retained = false;
                try
                {
                    if (Native.GetProcessIdOfThread(handle) != (uint)target.Id) { row.Status = "thread ID changed owner before handle opened"; continue; }
                    int status = Native.NtQueryInformationThread(handle, 9, out ulong address, 8, out _);
                    if (status == 0)
                    {
                        row.StartAddress = $"0x{address:x}";
                        row.Module = images.FirstOrDefault(m => m.Start <= address && address < m.End).Path ?? "unresolved";
                        row.Part = ownership.Classify(row.Module); row.Status = "measured";
                    }
                    else row.Status = $"start address unavailable: NTSTATUS 0x{status:x8}";
                    if (!Native.GetThreadTimes(handle, out long created, out _, out long kernel, out long user))
                    { row.Status += "; initial times unavailable: " + Marshal.GetLastWin32Error(); continue; }
                    held.Add((handle, created, user, kernel, Stopwatch.GetTimestamp(), row)); retained = true;
                }
                finally { if (!retained) Native.CloseHandle(handle); }
            }
            var window = Stopwatch.StartNew();
            while (window.Elapsed.TotalSeconds < seconds)
            {
                JobStop.Check();
                Thread.Sleep((int)Math.Min(1000, Math.Max(1, (seconds - window.Elapsed.TotalSeconds) * 1000)));
            }
            foreach (var before in held)
            {
                double elapsed = Stopwatch.GetElapsedTime(before.Timestamp).TotalSeconds;
                if (Native.GetThreadTimes(before.Handle, out long created, out long exited, out long kernel, out long user) && created == before.Created)
                {
                    before.Row.UserMilliseconds = (user - before.User) / 10000.0;
                    before.Row.KernelMilliseconds = (kernel - before.Kernel) / 10000.0;
                    before.Row.WindowSeconds = elapsed;
                    if (exited != 0) before.Row.Status += "; exited during window";
                }
                else before.Row.Status += "; final times unavailable: " + Marshal.GetLastWin32Error();
            }
            var original = records.Select(t => t.ThreadId).ToHashSet(); target.Refresh();
            foreach (ProcessThread thread in target.Threads)
            {
                if (records.Count >= 8192) throw new InvalidOperationException("8192 thread bound exceeded.");
                if (original.Contains(thread.Id)) continue;
                var row = new ThreadDelta { ThreadId = thread.Id, Status = "new during window; no baseline" };
                IntPtr handle = Native.OpenThread(0x40 | 0x800, false, thread.Id);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        if (Native.GetProcessIdOfThread(handle) != (uint)target.Id) { row.Status = "thread ID changed owner before handle opened"; records.Add(row); continue; }
                        if (Native.NtQueryInformationThread(handle, 9, out ulong address, 8, out _) == 0)
                        { row.StartAddress = $"0x{address:x}"; row.Module = images.FirstOrDefault(m => m.Start <= address && address < m.End).Path ?? "unresolved"; row.Part = ownership.Classify(row.Module); }
                    }
                    finally { Native.CloseHandle(handle); }
                }
                records.Add(row);
            }
        }
        finally { foreach (var item in held) Native.CloseHandle(item.Handle); }
        return records.OrderByDescending(t => t.UserMilliseconds + t.KernelMilliseconds).ToList();
    }

    internal static string Text(FootprintReport r)
    {
        var text = new StringBuilder();
        text.AppendLine($"pid={r.ProcessId} seconds={r.ElapsedSeconds:F3} speedscope={r.CpuSource}");
        text.AppendLine(r.Attribution); text.AppendLine(r.WorkingSetSemantics); text.AppendLine(r.ThreadSemantics);
        if (r.Cpu != null)
        {
            text.AppendLine($"\nCPU stack weights ({r.Cpu.Unit}), total={r.Cpu.TotalObservedStackWeight}; exclusive / inclusive");
            foreach (var part in r.Cpu.Parts) text.AppendLine($"part {part.Part}: {part.Exclusive:F3} / {part.Inclusive:F3}");
            foreach (var module in r.Cpu.Modules) text.AppendLine($"module {module.Part} {module.Module}: {module.Exclusive:F3} / {module.Inclusive:F3}");
            foreach (var note in r.Cpu.Notes) text.AppendLine(note);
        }
        text.AppendLine("\nThreads: tid | start | module | user ms | kernel ms | % one core | status");
        foreach (var t in r.Threads) text.AppendLine($"{t.ThreadId} | {t.StartAddress} | {t.Module} | {t.UserMilliseconds:F3} | {t.KernelMilliseconds:F3} | {t.PercentOfOneCore:F2} | {t.Status}");
        text.AppendLine("\nMemory totals by part (bytes):");
        foreach (var part in r.Parts) text.AppendLine(System.Text.Json.JsonSerializer.Serialize(part));
        text.AppendLine("\nImage/file memory: part | module | image committed | mapped committed | image resident | private image resident | failed query bytes");
        foreach (var m in r.Modules) text.AppendLine($"{m.Part} | {m.Module} ({m.Path}) | {m.ImageCommittedBytes} | {m.MappedCommittedBytes} | {m.ImageResidentBytes} | {m.ImagePrivateResidentBytes} | {m.WorkingSetQueryFailedBytes}");
        text.AppendLine("\nCommitted classes (private/mapped memory is not attributed to allocation owner):");
        foreach (var c in r.CommittedClasses) text.AppendLine($"{c.Class}: {c.CommittedBytes}");
        text.AppendLine($"\nManaged: {r.Managed.Status}: {r.Managed.Reason}"); text.AppendLine(r.Managed.Consistency);
        foreach (var a in r.Managed.Assemblies) text.AppendLine($"assembly {a.Part} {a.Assembly}: count={a.Count} bytes={a.Bytes}");
        text.AppendLine("30 most numerous managed types (no estimates):");
        foreach (var t in r.Managed.TopTypes) text.AppendLine($"{t.Count} objects, {t.Bytes} bytes: {t.Name} [{t.Assembly}] ({t.Part})");
        foreach (var note in r.Notes) text.AppendLine(note);
        text.AppendLine($"tool peak working set={r.ToolPeakWorkingSetBytes}; peak committed={r.ToolPeakCommittedBytes}");
        return text.ToString();
    }
}

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

internal sealed class WindowsStartupConfigurationReader
{
    private const int MaximumEntries = 131072;
    private static readonly Guid CommonStartup = new("82a5ea35-d9cd-47c5-9629-e15d2f714e6e");
    private static readonly Guid SchedulerClass = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");

    [SupportedOSPlatform("windows")]
    internal IReadOnlyList<WindowsConfiguredStartupEntry>? Read(WindowsPlatformDeclarationScope scope, string? sid, CancellationToken token,
        Action<WindowsOperatingSystemInformationFields>? sourceQueryAttempted = null)
    {
        var apartment = CoInitializeEx(0, 0);
        if (apartment < 0 && apartment != unchecked((int)0x80010106)) return null;
        try
        {
            var rows = new List<WindowsConfiguredStartupEntry>();
            if (scope.IncludeMachineConfiguration)
            {
                sourceQueryAttempted?.Invoke(WindowsOperatingSystemInformationFields.Startup);
                ReadFolder(ReadCommonStartupPath(), "machine", rows, token);
            }
            if (scope.IncludeUserConfiguration)
            {
                if (sid is null) return null;
                sourceQueryAttempted?.Invoke(WindowsOperatingSystemInformationFields.Startup);
                ReadFolder(ReadUserStartupPath(sid), "user-sid:" + sid, rows, token);
            }
            // Task registration belongs to the local scheduler namespace. Its actual
            // principal/trigger declarations remain in XML; do not assign caller SID.
            if (scope.IncludeTaskSchedulerConfiguration)
            {
                sourceQueryAttempted?.Invoke(WindowsOperatingSystemInformationFields.Startup);
                ReadTasks(rows, token);
            }
            token.ThrowIfCancellationRequested();
            return rows.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or COMException
            or TargetInvocationException or XmlException or DllNotFoundException or EntryPointNotFoundException)
        { return null; }
        finally { if (apartment >= 0) CoUninitialize(); }
    }

    private static string ReadCommonStartupPath()
    {
        nint path = 0; var id = CommonStartup;
        try
        {
            var hr = SHGetKnownFolderPath(in id, 0x4000, 0, out path); // DONT_VERIFY, never CREATE.
            if (hr < 0) throw new COMException("Common Startup path query failed.", hr);
            return Absolute(Marshal.PtrToStringUni(path));
        }
        finally { if (path != 0) Marshal.FreeCoTaskMem(path); }
    }

    [SupportedOSPlatform("windows")]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Startup registry key, folder or boot/logon task registration changed")]
    private static string ReadUserStartupPath(string sid)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var loaded = users.OpenSubKey(sid, false);
        if (loaded is null) throw new IOException("Requested user hive is not loaded.");
        const string explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
        using var shell = loaded.OpenSubKey(explorer + "Shell Folders", false);
        var expanded = shell?.GetValue("Startup", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.IsNullOrEmpty(expanded) && !expanded.Contains('%')) return Absolute(expanded);
        using var unexpanded = loaded.OpenSubKey(explorer + "User Shell Folders", false);
        var configured = unexpanded?.GetValue("Startup", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (configured is null) throw new IOException("Requested user has no readable Startup known-folder configuration.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var profile = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid, false);
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = profile?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            ["APPDATA"] = shell?.GetValue("AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            ["LOCALAPPDATA"] = shell?.GetValue("Local AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
        };
        return ResolveUserFolder(configured, map);
    }

    internal static string ResolveUserFolder(string configured, IReadOnlyDictionary<string, string?> targetUserKnownPaths)
    {
        var output = new System.Text.StringBuilder();
        for (var position = 0; position < configured.Length; position++)
        {
            if (configured[position] != '%') { output.Append(configured[position]); continue; }
            var end = configured.IndexOf('%', position + 1);
            if (end < 0 || !targetUserKnownPaths.TryGetValue(configured[(position + 1)..end], out var actual)
                || string.IsNullOrEmpty(actual) || actual.Contains('%'))
                throw new InvalidDataException("Target-user Startup path contains an unresolved configured variable.");
            output.Append(actual); position = end;
        }
        return Absolute(output.ToString());
    }
    private static string Absolute(string? path) => !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path)
        ? path : throw new InvalidDataException("Configured Startup folder is not an absolute actual path.");

    [SupportedOSPlatform("windows")]
    private static void ReadFolder(string folder, string scope, List<WindowsConfiguredStartupEntry> rows, CancellationToken token)
    {
        long characters = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested(); Bound(rows.Count);
                // desktop.ini is shell folder presentation metadata, not a launch entry.
                if (string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var declaration = string.Equals(Path.GetExtension(file), ".lnk", StringComparison.OrdinalIgnoreCase)
                    ? WindowsStartupShellLinkReader.Read(file) : new WindowsStartupFileDeclaration(file, "file", file, "", "");
                var payload = JsonSerializer.Serialize(declaration);
                characters += payload.Length;
                if (characters > 32 * 1024 * 1024) throw new InvalidDataException("Startup folder exceeded its complete-read text budget.");
                rows.Add(new(WindowsStartupConfigurationSource.StartupFolderFile, scope, file, payload));
            }
        }
        catch (DirectoryNotFoundException) { /* A configured but absent folder has no entries; no creation. */ }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadTasks(List<WindowsConfiguredStartupEntry> rows, CancellationToken token)
    {
        object? service = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromCLSID(SchedulerClass, throwOnError: true)!);
            if (service is null) throw new COMException("Task Scheduler service activation returned null.");
            _ = service.GetType().InvokeMember("Connect", BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod,
                null, service, [Type.Missing, Type.Missing, Type.Missing, Type.Missing], CultureInfo.InvariantCulture);
            var pending = new Stack<string>(); pending.Push(@"\");
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var inspected = 0; long characters = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); Bound(visited.Count);
                var path = pending.Pop(); if (!visited.Add(path)) throw new InvalidDataException("Repeated task folder prevents complete enumeration.");
                object? folder = null, tasks = null, children = null;
                try
                {
                    folder = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, [path]);
                    tasks = Invoke(folder, "GetTasks", BindingFlags.InvokeMethod, [1]); // TASK_ENUM_HIDDEN.
                    var taskCount = Count(tasks);
                    for (var index = 1; index <= taskCount; index++)
                    {
                        token.ThrowIfCancellationRequested(); Bound(inspected++);
                        object? task = null;
                        try
                        {
                            task = Invoke(tasks, "Item", BindingFlags.GetProperty, [index]);
                            var actualPath = Text(task, "Path"); var xml = Text(task, "Xml");
                            characters += xml.Length;
                            if (characters > 32 * 1024 * 1024) throw new InvalidDataException("Task enumeration exceeded its complete-read text budget.");
                            var declaration = DecodeTask(actualPath, xml);
                            if (declaration is not null) { Bound(rows.Count); rows.Add(declaration); }
                        }
                        finally { Release(task); }
                    }
                    children = Invoke(folder, "GetFolders", BindingFlags.InvokeMethod, [0]);
                    var childCount = Count(children);
                    for (var index = 1; index <= childCount; index++)
                    {
                        token.ThrowIfCancellationRequested(); Bound(pending.Count + visited.Count);
                        object? child = null;
                        try { child = Invoke(children, "Item", BindingFlags.GetProperty, [index]); pending.Push(Text(child, "Path")); }
                        finally { Release(child); }
                    }
                }
                finally { Release(children); Release(tasks); Release(folder); }
            }
        }
        finally { Release(service); }
    }

    internal static WindowsConfiguredStartupEntry? DecodeTask(string path, string xml)
    {
        if (path.Length == 0 || path[0] != '\\' || xml.Length == 0 || xml.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Incomplete task configuration.");
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        XNamespace schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = document.Root;
        if (task is null || task.Name != schema + "Task") throw new InvalidDataException("Task registration uses an unsupported schema.");
        var startup = task.Element(schema + "Triggers")?.Elements().Any(trigger => trigger.Name == schema + "BootTrigger" || trigger.Name == schema + "LogonTrigger") ?? false;
        if (!startup) return null;
        var principals = task.Element(schema + "Principals")?.Elements(schema + "Principal")
            .SelectMany(principal => principal.Elements().Where(element => element.Name == schema + "UserId" || element.Name == schema + "GroupId"))
            .Select(element => element.Name.LocalName + ":" + element.Value).ToArray() ?? [];
        return new(WindowsStartupConfigurationSource.TaskSchedulerBootOrLogon, "local-task-scheduler", path, xml, string.Join(";", principals));
    }
    private static void Bound(int count) { if (count >= MaximumEntries) throw new InvalidDataException("Startup enumeration exceeded its complete-read limit."); }
    [SupportedOSPlatform("windows")]
    private static object Invoke(object? target, string name, BindingFlags operation, object[] arguments)
        => target?.GetType().InvokeMember(name, operation | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, target, arguments, CultureInfo.InvariantCulture)
            ?? throw new COMException("Task configuration query returned null.");
    [SupportedOSPlatform("windows")]
    private static int Count(object value)
    { var count = Convert.ToInt32(Invoke(value, "Count", BindingFlags.GetProperty, []), CultureInfo.InvariantCulture); if (count < 0 || count > MaximumEntries) throw new InvalidDataException("Invalid task collection count."); return count; }
    [SupportedOSPlatform("windows")]
    private static string Text(object value, string name) => Invoke(value, name, BindingFlags.GetProperty, []) as string ?? throw new InvalidDataException("Task configuration is not text.");
    [SupportedOSPlatform("windows")]
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value); }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}

public sealed record WindowsStartupFileDeclaration(string EntryPath, string Kind, string ConfiguredTarget, string Arguments, string WorkingDirectory);

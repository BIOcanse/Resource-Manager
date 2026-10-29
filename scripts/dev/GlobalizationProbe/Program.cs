using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;
using ResourceManager.NativeUi.WebView;
using ResourceManager.ReadWrite.Windows.Gpu;

// Run through Invoke-WithMemoryLimit.ps1. Children inherit its owning Job Object.
if (args is ["--suite", var outputDirectory])
{
    Directory.CreateDirectory(outputDirectory);
    var assembly = typeof(Probe).Assembly.Location;
    var original = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json")))!;
    var modes = new[] { "icu", "nls", "invariant", "invariant-named" };
    var samples = new List<JsonNode>();
    var round = 0;
    while (round < 5)
    {
        var index = 0;
        while (index < modes.Length)
        {
            var mode = modes[(index++ + round) % modes.Length];
            var configuration = original.DeepClone();
            var properties = configuration["runtimeOptions"]!["configProperties"]!.AsObject();
            properties["System.Globalization.UseNls"] = mode == "nls";
            properties["System.Globalization.Invariant"] = mode.StartsWith("invariant", StringComparison.Ordinal);
            properties["System.Globalization.PredefinedCulturesOnly"] = mode != "invariant-named";
            var configPath = Path.Combine(outputDirectory, mode + ".runtimeconfig.json");
            File.WriteAllText(configPath, configuration.ToJsonString());
            var start = new ProcessStartInfo(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "exec", "--runtimeconfig", configPath, assembly, "--child" }
            };
            var configurationNames = new[] { "DOTNET_SYSTEM_GLOBALIZATION_USENLS", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT",
                "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY", "DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU" };
            var configurationIndex = 0;
            while (configurationIndex < configurationNames.Length)
                start.Environment.Remove(configurationNames[configurationIndex++]);
            var timer = Stopwatch.StartNew();
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Probe did not start.");
            try
            {
                var errors = child.StandardError.ReadToEndAsync();
                var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var startupMs = timer.Elapsed.TotalMilliseconds;
                var result = await child.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (ready != "ready" || child.ExitCode != 0)
                    throw new InvalidOperationException($"{mode}: exit={child.ExitCode}, stderr={await errors}, stdout={ready} {result}");
                var sample = JsonNode.Parse(result)!;
                sample["mode"] = mode;
                sample["round"] = round;
                sample["startupMs"] = startupMs;
                samples.Add(sample);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            }
        }
        round++;
    }
    File.WriteAllText(Path.Combine(outputDirectory, "measurements.json"), JsonSerializer.Serialize(new
    {
        runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        startupDefinition = "parent Process.Start through first stdout ready line after globalization workload; fresh processes, warm machine, no cache flushing",
        samples
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Saved {samples.Count} samples to {outputDirectory}");
    return;
}
if (args is not ["--child"]) throw new ArgumentException("Use --suite <registered output directory>.");

var workload = Probe.Exercise();
Console.WriteLine("ready");
using var current = Process.GetCurrentProcess();
current.Refresh();
var privateCommit = current.PrivateMemorySize64; // Sample before collector allocations; no forced GC or page touches.
var workingSet = current.WorkingSet64;
var mappings = Probe.Mappings();
Console.WriteLine(JsonSerializer.Serialize(new { privateCommit, workingSet, mappings, workload }));

internal static class Probe
{
    internal static object Exercise()
    {
        var failures = new List<string>();
        var namedCultures = new List<string>();
        var formatted = new Dictionary<string, string>();
        var languageIndex = 0;
        while (languageIndex < AppLanguage.SupportedIds.Count)
        {
            var language = AppLanguage.SupportedIds[languageIndex++];
            try
            {
                // Same named-culture lookup as SoftwareMetadataCatalogCompiler.NormalizeLanguage.
                var culture = CultureInfo.GetCultureInfo(language.Trim());
                namedCultures.Add(culture.Name);
                var text = ToolText.For(language);
                formatted[language] = text.Format("{0:N1}", 1234.5);
            }
            catch (CultureNotFoundException) { failures.Add(language + ":CultureNotFoundException"); }
        }
        // System language and LCID are used by AppLanguage and USB string-language selection.
        var systemLanguage = AppLanguage.Resolve("system");
        var lcid = CultureInfo.CurrentUICulture.LCID;
        var currentNumber = 1234.5.ToString("0.#", CultureInfo.CurrentCulture); // Native overlay.
        var linguisticOrder = new[] { "a", "A", "ä", "I", "ı", "Z", "a-b", "ab" }
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var ordinalOrder = new[] { "a", "A", "ä", "I", "ı", "Z", "a-b", "ab" }
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var wireNumber = 1234.5.ToString("R", CultureInfo.InvariantCulture);
        var wireTime = DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture);
        var key = ulong.Parse("134000000000000001", NumberStyles.None, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
        var token = "ADAPTER:I".ToLowerInvariant();
        var wire = JsonSerializer.Serialize(new { number = 1234.5, at = DateTimeOffset.UnixEpoch, key, token });
        // GPU matching depends on compatibility decomposition, not just an ASCII case fold.
        var normalizedGpuName = GpuAdapterNameClassifier.Normalize("ＡＭＤ ＲＡＤＥＯＮ ８９０Ｍ");
        var integratedGpu = GpuAdapterNameClassifier.IsLikelyIntegratedGpuName("ＡＭＤ ＲＡＤＥＯＮ ８９０Ｍ");
        // Real release parsing and shell-origin checks; local parsing only, with no network requests.
        if (!ReleaseVersion.TryParse("0.2.7-beta.20261029.1", out var version))
            throw new InvalidDataException("Release version did not parse.");
        var origin = new TrustedFrontendDocumentPolicy("http://127.0.0.1:48123/");
        var trustedApi = origin.IsTrustedApiRequest("http://127.0.0.1:48123/api/metrics");
        string? idnHost = null;
        var rejectsRemoteIdn = false;
        try
        {
            rejectsRemoteIdn = !origin.IsTrustedDocument("http://bücher.example/");
            idnHost = new Uri("https://bücher.example/metadata").IdnHost;
        }
        catch (ArgumentException exception) { failures.Add("IDN:" + exception.GetType().Name); }
        return new { systemLanguage, lcid, currentCulture = CultureInfo.CurrentCulture.Name, currentNumber,
            namedCultures, failures, formatted, linguisticOrder, ordinalOrder, wireNumber, wireTime, wire, idnHost,
            trustedApi, rejectsRemoteIdn, releaseSeries = version!.Series, normalizedGpuName, integratedGpu };
    }

    [StructLayout(LayoutKind.Sequential)] private struct Mbi
    {
        public ulong Base, AllocationBase;
        public uint AllocationProtect, PartitionAndPadding;
        public ulong Size;
        public uint State, Protect, Type, Padding;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Page { public ulong Address, Flags; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(IntPtr process, ulong address, out Mbi info, nuint size);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetMappedFileNameW(IntPtr process, ulong address, StringBuilder path, int size);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool QueryWorkingSetEx(IntPtr process, [In, Out] Page[] pages, int size);

    internal static Dictionary<string, long[]> Mappings()
    {
        var result = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["icudtl.dat"] = [0, 0], ["icu.dll"] = [0, 0], ["SortDefault.nls"] = [0, 0]
        };
        var handle = GetCurrentProcess();
        ulong address = 0;
        var count = 0;
        var pageSize = Environment.SystemPageSize;
        while (address < 0x7fffffff0000 && count++ < 200000)
        {
            if (VirtualQueryEx(handle, address, out var info, (nuint)Marshal.SizeOf<Mbi>()) == 0)
            {
                if (Marshal.GetLastWin32Error() == 87) break;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var next = checked(info.Base + info.Size);
            if (next <= address) throw new InvalidDataException("VirtualQueryEx did not advance.");
            if (info.State == 0x1000 && info.Type is 0x40000 or 0x1000000)
            {
                var path = new StringBuilder(4096);
                // Page-file-backed shared sections have no mapped file name.
                if (GetMappedFileNameW(handle, info.Base, path, path.Capacity) != 0
                    && result.TryGetValue(Path.GetFileName(path.ToString()), out var bytes))
                {
                    bytes[0] += checked((long)info.Size);
                    var pages = new Page[checked((int)(info.Size / (ulong)pageSize))];
                    var i = 0;
                    while (i < pages.Length) { pages[i].Address = info.Base + (ulong)i * (ulong)pageSize; i++; }
                    if (!QueryWorkingSetEx(handle, pages, checked(pages.Length * Marshal.SizeOf<Page>())))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    i = 0;
                    while (i < pages.Length) { if ((pages[i++].Flags & 1) != 0) bytes[1] += pageSize; }
                }
            }
            address = next;
        }
        if (count >= 200000) throw new InvalidDataException("Region bound exceeded.");
        return result; // Each pair is [committed mapped bytes, resident bytes (WS Valid=1)].
    }
}

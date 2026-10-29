using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MemoryMeasurement;

internal static class Program
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Expected duplicates, footprint, fixture or selftest.");
            if (args[0] == "fixture") { Fixture.Run(args[1], int.Parse(args[2])); return 0; }
            if (args[0] == "selftest") { SelfTest.Run(args[1]); return 0; }
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); int i = 1;
            while (i < args.Length)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected --option value pairs.");
                options.Add(args[i][2..], args[i + 1]); i += 2;
            }
            string Get(string key, string fallback = "") => options.TryGetValue(key, out var value) ? value : fallback;
            int pid = int.Parse(Get("pid")); string directory = Path.GetFullPath(Get("out"));
            if (pid <= 0 || pid == Environment.ProcessId) throw new ArgumentException("Supply a positive target PID other than this tool.");
            Directory.CreateDirectory(directory);
            string? clrMd = Get("clrmd"); if (clrMd.Length == 0) clrMd = null;
            if (args[0] == "duplicates")
            {
                int slice = int.Parse(Get("slice", "4096")), top = int.Parse(Get("top", "30")), sample = int.Parse(Get("sample", "96"));
                if (slice < 64 || slice > 65536 || (slice & (slice - 1)) != 0 || top < 1 || top > 100 || sample < 0 || sample > 65536)
                    throw new ArgumentException("slice: power of two 64..65536; top: 1..100; sample: 0..65536.");
                var result = Duplicates.Run(pid, directory, slice, bool.Parse(Get("separate-zero", "true")), top, sample, clrMd);
                Write(directory, "duplicates.json", result); File.WriteAllText(Path.Combine(directory, "summary.txt"), Duplicates.Text(result), new UTF8Encoding(false));
                Console.WriteLine(Duplicates.Text(result));
            }
            else if (args[0] == "footprint")
            {
                int seconds = int.Parse(Get("seconds", "30")); if (seconds < 1 || seconds > 3600) throw new ArgumentException("seconds: 1..3600.");
                string? speedscope = Get("speedscope"); if (speedscope.Length == 0) speedscope = null;
                var result = Footprint.Run(pid, speedscope, seconds, Get("repo"), clrMd, Get("own").Split(';', StringSplitOptions.RemoveEmptyEntries));
                Write(directory, "footprint.json", result); File.WriteAllText(Path.Combine(directory, "footprint.txt"), Footprint.Text(result), new UTF8Encoding(false));
                Console.WriteLine(Footprint.Text(result));
            }
            else throw new ArgumentException("Unknown measurement: " + args[0]);
            return 0;
        }
        catch (OperationCanceledException error) { Console.Error.WriteLine(error.Message); return 75; }
        catch (Win32Exception error) when (error.NativeErrorCode == 5) { Console.Error.WriteLine(error); return 5; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    internal static void Write<T>(string directory, string filename, T value)
    {
        using var file = new FileStream(Path.Combine(directory, filename), FileMode.Create, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, JsonOptions);
    }
}

internal sealed class ProbeObject { public long Id; public long Payload; }
internal static class Fixture
{
    internal static void Run(string readyFile, int megabytes)
    {
        if (megabytes < 1 || megabytes > 500) throw new ArgumentOutOfRangeException(nameof(megabytes));
        const int page = 4096, copies = 257, zeroPages = 19, objectCount = 12345;
        ulong duplicate = Native.VirtualAlloc(0, page * copies, 0x3000, 4);
        ulong zero = Native.VirtualAlloc(0, page * zeroPages, 0x3000, 4);
        ulong filler = Native.VirtualAlloc(0, (nuint)megabytes * 1024 * 1024, 0x3000, 4);
        ulong protectedPages = Native.VirtualAlloc(0, page * 2, 0x3000, 4);
        if (duplicate == 0 || zero == 0 || filler == 0 || protectedPages == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var pattern = new byte[page]; new Random(123456789).NextBytes(pattern);
            Encoding.ASCII.GetBytes("RM-MEMORY-SCAN-EXACT-DUPLICATE-257").CopyTo(pattern, 0);
            Encoding.Unicode.GetBytes("memory duplicate").CopyTo(pattern, 64);
            int i = 0; while (i < copies) { Marshal.Copy(pattern, 0, (IntPtr)(duplicate + (ulong)i * page), page); i++; }
            // Unique first eight bytes for every filler page; all 500 MiB really touched, not merely reserved.
            i = 0; int pages = megabytes * 256;
            while (i < pages)
            {
                BitConverter.TryWriteBytes(pattern.AsSpan(0, 8), (ulong)i + 0x3141592600000000);
                Marshal.Copy(pattern, 0, (IntPtr)(filler + (ulong)i * page), page); i++;
            }
            if (!Native.VirtualProtect(protectedPages, page, 1, out _) || !Native.VirtualProtect(protectedPages + page, page, 0x104, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var objects = new ProbeObject[objectCount]; i = 0;
            while (i < objects.Length) { objects[i] = new ProbeObject { Id = i, Payload = i * 17 }; i++; }
            Program.Write(Path.GetDirectoryName(readyFile)!, Path.GetFileName(readyFile), new {
                ProcessId = Environment.ProcessId, SliceBytes = page, DuplicateAddress = duplicate, DuplicateCopies = copies,
                DuplicateBytes = copies * page, RedundantBytes = (copies - 1) * page, ZeroAddress = zero, ZeroBytes = zeroPages * page,
                FillerAddress = filler, FillerBytes = (ulong)megabytes * 1024 * 1024, ProtectedAddress = protectedPages,
                ObjectType = typeof(ProbeObject).FullName, ObjectCount = objectCount });
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            Console.WriteLine("ready"); Console.ReadLine(); GC.KeepAlive(objects);
        }
        finally
        {
            Native.VirtualFree(duplicate, 0, 0x8000); Native.VirtualFree(zero, 0, 0x8000);
            Native.VirtualFree(filler, 0, 0x8000); Native.VirtualFree(protectedPages, 0, 0x8000);
        }
    }
}

internal static class SelfTest
{
    static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException("Self-test: " + message); }
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Duplicates.TestCollisionConfirmation(directory);
        Console.WriteLine("Full-byte collision partition and changed-candidate exclusion: PASS.");
        var ownership = new Ownership(directory, ["NativeFromRepo"]);
        ownership.Observe(@"C:\Windows\System32\kernel32.dll");
        ownership.Observe(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.8\System.Private.CoreLib.dll");
        ownership.Observe(@"D:\app\Microsoft.Diagnostics.Tracing.TraceEvent.dll");
        ownership.Observe(@"D:\app\coreclr.dll");
        ownership.Observe(@"D:\app\dotnet.exe");
        ownership.Observe(@"C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App\10.0.8\Microsoft.AspNetCore.Http.dll");
        ownership.Observe(@"C:\Windows\System32\same-name.dll");
        ownership.Observe(@"D:\app\same-name.dll");
        Require(ownership.Classify("ResourceManager.App") == "own" && ownership.Classify("NativeFromRepo.dll") == "own", "own code attribution");
        Require(ownership.Classify("kernel32") == "runtime-windows" && ownership.Classify("System.Private.CoreLib") == "runtime-windows", "runtime/Windows attribution");
        Require(ownership.Classify("Microsoft.AspNetCore.Http") == "runtime-windows", "ASP.NET shared framework attribution");
        Require(ownership.Classify("coreclr") == "runtime-windows" && ownership.Classify("dotnet") == "runtime-windows", "app-bundled runtime and host attribution");
        Require(Ownership.IsPlatformProduct("Microsoft\u00ae .NET", "Microsoft Corporation") &&
            Ownership.IsPlatformProduct("Microsoft\u00ae Windows\u00ae Operating System", "Microsoft Corporation") &&
            !Ownership.IsPlatformProduct("Microsoft.CodeAnalysis", "Microsoft Corporation") &&
            !Ownership.IsPlatformProduct("Microsoft.Diagnostics.Tracing.TraceEvent", "Microsoft Corporation"), "runtime product metadata excludes compiler/TraceEvent dependencies");
        Require(Ownership.ProductPart("runtime-windows", "GPU vendor DLL", "NVIDIA Corporation") == "external" &&
            Ownership.ProductPart("external", "Microsoft\u00ae .NET", "Microsoft Corporation") == "runtime-windows", "vendor metadata overrides Windows placement and recognizes bundled .NET");
        Require(ownership.Classify("Microsoft.Diagnostics.Tracing.TraceEvent") == "external", "Microsoft-named external dependency is not the runtime");
        Require(ownership.Classify("same-name") == "unresolved" && ownership.Classify(@"D:\app\same-name.dll") == "external" &&
            ownership.Classify(@"C:\Windows\System32\same-name.dll") == "runtime-windows", "ambiguous module name preserves full-path ownership");
        Console.WriteLine("Own/external/runtime module attribution and ambiguous-name handling: PASS.");
        string path = Path.Combine(directory, "cpu-contract.speedscope.json");
        const string json = """
        {"shared":{"frames":[{"name":"ResourceManager.App!Root"},{"name":"TraceEvent.dll!External"},{"name":"System.Private.CoreLib!Runtime"},{"name":"native_unknown"},{"name":"ResourceManager.App!Nested"}]},"profiles":[
        {"type":"sampled","name":"sampled","unit":"milliseconds","startValue":0,"endValue":10,"samples":[[0,1,0],[0,2],[3]],"weights":[2,3,5]},
        {"type":"evented","name":"evented","unit":"microseconds","startValue":0,"endValue":10000,"events":[{"type":"O","at":0,"frame":0},{"type":"O","at":2000,"frame":1},{"type":"C","at":5000,"frame":1},{"type":"O","at":8000,"frame":4}]}
        ]}
        """;
        File.WriteAllText(path, json);
        string Classify(string module) => module.StartsWith("ResourceManager") ? "own" : module.StartsWith("System") ? "runtime-windows" : module == "unresolved" ? "unresolved" : "external";
        var result = CpuSamples.Read(path, Classify);
        var own = result.Parts.Single(p => p.Part == "own"); var ext = result.Parts.Single(p => p.Part == "external");
        Require(result.TotalObservedStackWeight == 20, "total stack weights");
        Require(own.Exclusive == 9 && own.Inclusive == 15, "own exclusive/inclusive dedup and event tail");
        Require(ext.Exclusive == 3 && ext.Inclusive == 5, "external exclusive/inclusive");
        Require(result.Modules.Single(m => m.Module == "ResourceManager.App").Inclusive == 15, "recursive module charged once");
        File.WriteAllText(Path.Combine(directory, "cpu-contract-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        string invalid = Path.Combine(directory, "cpu-invalid.speedscope.json");
        File.WriteAllText(invalid, json.Replace("\"frame\":1}", "\"frame\":99}"));
        bool rejected = false; try { CpuSamples.Read(invalid, Classify); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "invalid frame references must fail");
        Console.WriteLine("CPU aggregation contract: PASS (sampled, evented, recursive, cross-part, unresolved, unit conversion, tail, invalid frame).");
    }
}

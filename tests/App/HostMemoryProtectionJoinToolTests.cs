using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

// The only affected process is the child this fixture starts; every file lives in its own directories.
public sealed class HostMemoryProtectionJoinToolTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rm-memory-join-" + Guid.NewGuid().ToString("N"));
    private string SecondDirectory => directory + "-second";
    private static string ToolPath([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../scripts/tools/Join-MemorySlice.ps1"));

    [Theory]
    [InlineData(false, "paths")]
    [InlineData(true, "paths")]
    [InlineData(false, "record")]
    [InlineData(true, "record")]
    [InlineData(false, "first")]
    [InlineData(true, "first")]
    [InlineData(false, "missing-tail")]
    [InlineData(true, "missing-tail")]
    public void RealSegmentedDumpJoinsByteForByteForDbgHelpOrRejectsMissingTail(bool powerShell7, string mode)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(SecondDirectory);
        using var child = HostMemoryProtectionExecutorTests.StartOwnedChild();
        try
        {
            using var allocationHandle = HostMemoryProtectionNative.OpenProcess(0x0008 | 0x0020, false, child.Id);
            Assert.False(allocationHandle.IsInvalid);
            var allocation = VirtualAllocEx(allocationHandle, 0, 65536, 0x3000, 0x04);
            Assert.NotEqual((nuint)0, allocation);
            var marker = "joined-private-slice-marker"u8.ToArray();
            Assert.True(WriteProcessMemory(allocationHandle, allocation, marker, (nuint)marker.Length, out var written));
            Assert.Equal((nuint)marker.Length, written);
            using var reserve = new HostMemoryDiskReserves();
            var status = reserve.Prepare([new(directory, 64 * 1024), new(SecondDirectory, 32 * 1024 * 1024)]);
            Assert.True(status.Built, status.Reason);
            using var target = new WindowsHostMemoryProtectionExecutor().Open(HostMemoryProtectionExecutorTests.Identity(child));
            Assert.Null(target.ExclusionReason);
            var action = target.Execute(reserve);
            Assert.True(action.Outcome is "completed" or "completed-with-release-limit", $"{action.Outcome}: {action.Reason}");
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(2, action.DumpPaths.Count);
            var sourceHashes = action.DumpPaths.Select(HashFile).ToArray();
            var recordPath = Path.Combine(directory, "action.json");
            File.WriteAllText(recordPath, JsonSerializer.Serialize(new { dumpPaths = action.DumpPaths }), Encoding.UTF8);
            var output = Path.Combine(directory, "joined.dmp");
            var input = mode switch
            {
                "record" => "-RecordPath " + Literal(recordPath),
                "first" => "-FirstSegmentPath " + Literal(action.DumpPaths[0]) + " -SegmentDirectories " + ArrayLiteral([directory, SecondDirectory]),
                "missing-tail" => "-FirstSegmentPath " + Literal(action.DumpPaths[0]),
                _ => "-DumpPaths " + ArrayLiteral(action.DumpPaths)
            };
            var result = RunTool(powerShell7, input + " -OutputPath " + Literal(output));
            if (mode == "missing-tail")
            {
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("incomplete", result.StdErr, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(output));
                Assert.Equal(sourceHashes, action.DumpPaths.Select(HashFile).ToArray());
                return;
            }
            Assert.True(result.ExitCode == 0, result.StdErr);
            using var receipt = JsonDocument.Parse(result.StdOut);
            Assert.Equal(output, receipt.RootElement.GetProperty("outputPath").GetString());
            Assert.Equal(action.BytesWritten, receipt.RootElement.GetProperty("bytesWritten").GetUInt64());
            Assert.Equal(2, receipt.RootElement.GetProperty("segmentCount").GetInt32());
            AssertLogicalBytes(action.DumpPaths, output);
            Assert.Equal(sourceHashes, action.DumpPaths.Select(HashFile).ToArray());
            lock (HostMemoryMinidumpWriter.DbgHelpGate)
                HostMemoryProtectionExecutorTests.AssertStandardDumpAndPrivateMarker([output], allocation, marker);
        }
        finally { HostMemoryProtectionExecutorTests.StopOwnedChild(child); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPathsPreserveRecordOrderAndFirstPathDiscoversNumberedSiblings(bool powerShell7)
    {
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "literal[slice].dmp");
        var second = first + ".part0002";
        var third = first + ".part0003";
        File.WriteAllBytes(first, Enumerable.Range(0, 1048583).Select(index => (byte)(index % 251)).ToArray());
        WriteMemory64Header(first, 1048583 + 5);
        File.WriteAllBytes(third, [3, 4, 5]); // Creation order must not determine discovery order.
        File.WriteAllBytes(second, [2, 8]);
        var discovered = Path.Combine(directory, "discovered.dmp");
        var result = RunTool(powerShell7, "-FirstSegmentPath " + Literal(first) + " -OutputPath " + Literal(discovered));
        Assert.True(result.ExitCode == 0, result.StdErr);
        AssertLogicalBytes([first, second, third], discovered);
        var ordered = Path.Combine(directory, "ordered.dmp");
        // Explicit records define the order; filenames are not sorted or guessed in this mode.
        result = RunTool(powerShell7, "-DumpPaths " + ArrayLiteral([third, first, second]) + " -OutputPath " + Literal(ordered));
        Assert.True(result.ExitCode == 0, result.StdErr);
        AssertLogicalBytes([third, first, second], ordered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidInputsFailWithoutChangingSourcesOrOverwritingExistingOutput(bool powerShell7)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(SecondDirectory);
        var first = Path.Combine(directory, "slice.dmp");
        var second = first + ".part0002";
        var third = first + ".part0003";
        var output = Path.Combine(directory, "output.dmp");
        File.WriteAllBytes(first, [1, 2, 3]);
        var initialHash = HashFile(first);
        var missing = Path.Combine(directory, "missing.dmp");
        AssertFailure("-DumpPaths " + ArrayLiteral([first, missing]), "output.dmp");
        AssertFailure("-DumpPaths " + ArrayLiteral([first, first]), "output.dmp");
        AssertFailure("-DumpPaths " + ArrayLiteral([first]), "slice.dmp");
        var empty = Path.Combine(directory, "empty.dmp");
        File.WriteAllBytes(empty, []);
        AssertFailure("-DumpPaths " + ArrayLiteral([first, empty]), "output.dmp");
        var record = Path.Combine(directory, "invalid.json");
        File.WriteAllText(record, "{\"dumpPaths\":\"not-an-array\"}");
        AssertFailure("-RecordPath " + Literal(record), "output.dmp");
        File.WriteAllBytes(third, [3]);
        AssertFailure("-FirstSegmentPath " + Literal(first), "output.dmp");
        File.WriteAllBytes(second, [2]);
        File.WriteAllBytes(Path.Combine(SecondDirectory, Path.GetFileName(second)), [9]);
        AssertFailure("-FirstSegmentPath " + Literal(first) + " -SegmentDirectories " + ArrayLiteral([directory, SecondDirectory]), "output.dmp");
        File.WriteAllBytes(output, [42]);
        AssertFailure("-DumpPaths " + ArrayLiteral([first]), "output.dmp");
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(output));
        Assert.Equal(initialHash, HashFile(first));

        void AssertFailure(string arguments, string outputName)
        {
            var destination = Path.Combine(directory, outputName);
            var existed = File.Exists(destination);
            var result = RunTool(powerShell7, arguments + " -OutputPath " + Literal(destination));
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StdErr));
            if (!existed) Assert.False(File.Exists(destination));
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunTool(bool powerShell7, string arguments)
    {
        // A wrapper passes actual PowerShell arrays on both hosts; native -File treats array arguments differently.
        var wrapper = Path.Combine(directory, "invoke-join.ps1");
        File.WriteAllText(wrapper, "& " + Literal(ToolPath()) + " " + arguments + Environment.NewLine + "exit $LASTEXITCODE", Encoding.UTF8);
        var executable = powerShell7 ? "pwsh.exe" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper }
        }) ?? throw new InvalidOperationException("Could not start the test-owned PowerShell child.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(process.WaitForExit(30000), "Join-MemorySlice.ps1 exceeded the test timeout.");
            return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); Assert.True(process.WaitForExit(5000)); }
        }
    }

    private static void AssertLogicalBytes(IReadOnlyList<string> paths, string output)
    {
        using var joined = File.OpenRead(output);
        var sourceBytes = new byte[65536];
        var joinedBytes = new byte[sourceBytes.Length];
        foreach (var path in paths)
        {
            using var source = File.OpenRead(path);
            int count;
            while ((count = source.Read(sourceBytes)) != 0)
            {
                joined.ReadExactly(joinedBytes.AsSpan(0, count));
                Assert.True(sourceBytes.AsSpan(0, count).SequenceEqual(joinedBytes.AsSpan(0, count)), "Joined bytes differ from the ordered source stream.");
            }
        }
        Assert.Equal(joined.Length, joined.Position);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteMemory64Header(string path, ulong totalLength)
    {
        using var stream = File.OpenWrite(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x504d444du); // MINIDUMP_HEADER: one directory entry at offset 32.
        writer.Write(0x0000a793u);
        writer.Write(1u);
        writer.Write(32u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0UL);
        writer.Write(9u); // MINIDUMP_DIRECTORY: Memory64List, 32 bytes at offset 44.
        writer.Write(32u);
        writer.Write(44u);
        writer.Write(1UL); // One memory region; payload follows the descriptor at offset 76.
        writer.Write(76UL);
        writer.Write(4096UL);
        writer.Write(totalLength - 76);
    }
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string ArrayLiteral(IEnumerable<string> values) => "@(" + string.Join(",", values.Select(Literal)) + ")";

    public void Dispose()
    {
        foreach (var ownedDirectory in new[] { directory, SecondDirectory })
        {
            if (!Directory.Exists(ownedDirectory)) continue;
            foreach (var file in Directory.EnumerateFiles(ownedDirectory)) File.Delete(file);
            Directory.Delete(ownedDirectory);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualAllocEx(SafeProcessHandle process, nuint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint bytesWritten);
}

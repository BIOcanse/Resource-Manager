using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

internal static class WindowsDumpAnalysisReader
{
    internal static void Validate(WindowsDumpAnalysisRequest request)
    {
        if (request.Timeout < TimeSpan.FromMilliseconds(100) || request.Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be 100ms to 2 minutes.");
        if (request.MaximumFrames is < 1 or > 64 || request.MaximumOutputBytes is < 4096 or > 131072)
            throw new ArgumentOutOfRangeException(nameof(request), "Frames must be 1 to 64; output must be 4096 to 131072 bytes.");
        foreach (var path in new[] { request.DumpPath, request.HelperExecutablePath, request.EngineLibraryPath })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0'))
                throw new ArgumentException("Supply absolute dump, helper and engine paths.", nameof(request));
        if (!string.Equals(Path.GetFileName(request.EngineLibraryPath), "dbgeng.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EngineLibraryPath must identify the installed dbgeng.dll.", nameof(request));
        if (request.LocalSymbolDirectories.IsDefault || request.LocalSymbolDirectories.Length > 16)
            throw new ArgumentException("Supply an explicit symbol-directory list, possibly empty, with at most 16 entries.", nameof(request));
        foreach (var path in request.LocalSymbolDirectories)
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.IndexOfAny([';', '*', '\0']) >= 0 || !Directory.Exists(path))
                throw new ArgumentException("Symbols must be existing local directories, without symbol-server syntax.", nameof(request));
    }

    internal static async ValueTask<WindowsDumpAnalysisResult> ReadAsync(string resourceIdentity, WindowsDumpAnalysisRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return Empty(resourceIdentity, WindowsDumpAnalysisStatus.Unsupported, "Windows DbgEng is required.");
        try
        {
            foreach (var path in new[] { request.HelperExecutablePath, request.EngineLibraryPath })
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    return Empty(resourceIdentity, WindowsDumpAnalysisStatus.MissingDependency, "A dependency path identifies a directory.");
        }
        catch (UnauthorizedAccessException) { return Empty(resourceIdentity, WindowsDumpAnalysisStatus.PermissionDenied, "The explicit helper or engine is not accessible to this caller."); }
        catch (IOException) { return Empty(resourceIdentity, WindowsDumpAnalysisStatus.MissingDependency, "The explicit helper or installed dbgeng.dll is missing or inaccessible."); }
        try
        {
            // Readability only. The host owns path, SID and ticket authorization.
            using (var input = new FileStream(request.DumpPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (input.Length == 0) return Empty(resourceIdentity, WindowsDumpAnalysisStatus.Unreadable, "The supplied dump is empty.");
        }
        catch (UnauthorizedAccessException) { return Empty(resourceIdentity, WindowsDumpAnalysisStatus.PermissionDenied, "The supplied dump is not readable by this caller."); }
        catch (IOException) { return Empty(resourceIdentity, WindowsDumpAnalysisStatus.Unreadable, "The supplied existing dump cannot be opened."); }
        var start = new ProcessStartInfo(request.HelperExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(request.HelperExecutablePath)!
        };
        start.Environment.Remove("_NT_SYMBOL_PATH");
        start.Environment.Remove("_NT_ALT_SYMBOL_PATH");
        start.Environment.Remove("_NT_EXECUTABLE_IMAGE_PATH");
        foreach (var arg in new[] { request.EngineLibraryPath, request.DumpPath, string.Join(';', request.LocalSymbolDirectories),
            ((uint)request.Timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.MaximumFrames.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Timeout);
        try
        {
            if (!process.Start()) return Empty(resourceIdentity, WindowsDumpAnalysisStatus.MissingDependency, "The explicit helper could not start.");
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, request.MaximumOutputBytes, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) return Empty(resourceIdentity, WindowsDumpAnalysisStatus.Unreadable, "The analysis helper failed without a complete result.");
            return Decode(output.Result, request.MaximumFrames, resourceIdentity);
        }
        catch (OperationCanceledException)
        {
            StopOwned(process);
            token.ThrowIfCancellationRequested();
            return Empty(resourceIdentity, WindowsDumpAnalysisStatus.TimedOut, "The explicit analysis exceeded its deadline; owned helper terminated.");
        }
        catch (InvalidDataException) { StopOwned(process); return Empty(resourceIdentity, WindowsDumpAnalysisStatus.InvalidEngineOutput, "The helper output exceeded a limit or violated its wire contract."); }
        catch (UnauthorizedAccessException) { StopOwned(process); return Empty(resourceIdentity, WindowsDumpAnalysisStatus.PermissionDenied, "The explicit helper could not be executed by this caller."); }
        catch (Win32Exception) { StopOwned(process); return Empty(resourceIdentity, WindowsDumpAnalysisStatus.MissingDependency, "The helper is unavailable or incompatible with this process platform."); }
        catch (IOException) { StopOwned(process); return Empty(resourceIdentity, WindowsDumpAnalysisStatus.InvalidEngineOutput, "The helper output was incomplete."); }
    }
    private static void StopOwned(Process process)
    {
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
        catch (InvalidOperationException) { /* Never started or already exited. */ }
        catch (Win32Exception) { /* Already exiting; no unrelated process is touched. */ }
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return result.ToArray();
            if (result.Length + read > limit) throw new InvalidDataException("Output limit exceeded.");
            result.Write(buffer, 0, read);
        }
    }
    internal static WindowsDumpAnalysisResult Decode(byte[] data, int maximumFrames, string resourceIdentity)
    {
        if (maximumFrames is < 1 or > 64 || data.Length > 131072) throw new InvalidDataException("Invalid decoder bounds.");
        using var stream = new MemoryStream(data, false);
        using var read = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (read.ReadUInt32() != 0x524D4441 || read.ReadUInt32() != 1) throw new InvalidDataException("Unknown helper ABI.");
        var overall = read.ReadInt32();
        if (overall != 0)
        {
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing failed-result data.");
            return Empty(resourceIdentity, overall == unchecked((int)0x80070005) ? WindowsDumpAnalysisStatus.PermissionDenied
                : overall == unchecked((int)0x8007007E) ? WindowsDumpAnalysisStatus.MissingDependency
                : overall == unchecked((int)0x80004001) ? WindowsDumpAnalysisStatus.Unsupported
                : overall == unchecked((int)0x800705B4) ? WindowsDumpAnalysisStatus.TimedOut
                : WindowsDumpAnalysisStatus.Unreadable, $"DbgEng HRESULT 0x{overall:X8}.");
        }
        var bugHr = read.ReadInt32(); var exceptionHr = read.ReadInt32(); var contextHr = read.ReadInt32(); var stackHr = read.ReadInt32();
        var processor = read.ReadUInt32(); var code = read.ReadUInt32();
        var bug = ImmutableArray.Create(read.ReadUInt64(), read.ReadUInt64(), read.ReadUInt64(), read.ReadUInt64());
        var pid = read.ReadUInt32(); var tid = read.ReadUInt32(); var exceptionCode = read.ReadUInt32(); var flags = read.ReadUInt32();
        var address = read.ReadUInt64(); var firstChance = read.ReadUInt32();
        if (firstChance > 1) throw new InvalidDataException("Invalid first chance flag.");
        var parameterCount = read.ReadUInt32();
        if (parameterCount > 15) throw new InvalidDataException("Invalid exception parameter count.");
        var parameters = ImmutableArray.CreateBuilder<ulong>((int)parameterCount);
        for (var i = 0; i < parameterCount; i++) parameters.Add(read.ReadUInt64());
        var contextLength = read.ReadUInt32();
        if (contextLength > 4096 || (contextHr == 0 && (processor != 0x8664 || contextLength != 1232))
            || (contextHr != 0 && contextLength != 0)) throw new InvalidDataException("Invalid AMD64 CONTEXT boundary.");
        var context = read.ReadBytes((int)contextLength);
        if (context.Length != contextLength) throw new InvalidDataException("Truncated thread context.");
        var count = read.ReadUInt32();
        if (count > maximumFrames || (stackHr != 0 && count != 0)) throw new InvalidDataException("Invalid stack count.");
        var frames = ImmutableArray.CreateBuilder<WindowsDumpStackFrame>((int)count);
        for (var i = 0; i < count; i++)
        {
            var ip = read.ReadUInt64(); var ra = read.ReadUInt64(); var frame = read.ReadUInt64(); var sp = read.ReadUInt64(); var displacement = read.ReadUInt64();
            frames.Add(new(ip, ra, frame, sp, ReadText(read), displacement, ReadText(read)));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing helper data.");
        var stack = frames.ToImmutable();
        var symbolized = stack.Any(static frame => frame.Symbol is not null);
        var bugStatus = bugHr == 0 ? WindowsDumpFieldStatus.Available : bugHr == unchecked((int)0x80004001) ? WindowsDumpFieldStatus.Unsupported : WindowsDumpFieldStatus.Unavailable;
        var exceptionStatus = exceptionHr == 0 ? WindowsDumpFieldStatus.Available : exceptionHr == unchecked((int)0x80004001) ? WindowsDumpFieldStatus.Unsupported : WindowsDumpFieldStatus.Unavailable;
        var stackStatus = stackHr != 0 ? WindowsDumpFieldStatus.Unavailable : symbolized ? WindowsDumpFieldStatus.Available : WindowsDumpFieldStatus.SymbolsUnavailable;
        var result = Empty(resourceIdentity, bugStatus == WindowsDumpFieldStatus.Available && exceptionStatus == WindowsDumpFieldStatus.Available && symbolized
            ? WindowsDumpAnalysisStatus.Available : WindowsDumpAnalysisStatus.Partial,
            $"Bugcheck=0x{bugHr:X8}; exception=0x{exceptionHr:X8}; context=0x{contextHr:X8}; stack=0x{stackHr:X8}.");
        return result with {
            BugcheckCodeReported = bugHr == 0 ? code : null, BugcheckParametersReported = bugHr == 0 ? bug : null,
            ExceptionContextInterpreted = exceptionHr == 0 ? new(processor, pid, tid, exceptionCode, flags, address, firstChance != 0,
                parameters.ToImmutable(), contextHr == 0 ? ImmutableArray.Create(context) : null,
                contextHr == 0 ? WindowsDumpFieldStatus.Available : contextHr == unchecked((int)0x80004001)
                    ? WindowsDumpFieldStatus.Unsupported : WindowsDumpFieldStatus.Unavailable) : null,
            SymbolizedStackInterpreted = symbolized ? stack : null,
            SuspectedModuleInterpreted = stack.FirstOrDefault(static f => f.Symbol is not null && f.Module is not null)?.Module,
            BugcheckStatus = bugStatus, ExceptionStatus = exceptionStatus, StackStatus = stackStatus };
    }
    private static string? ReadText(BinaryReader read)
    {
        var length = read.ReadUInt32();
        if (length > 2048) throw new InvalidDataException("Symbol/module name exceeds bound.");
        var bytes = read.ReadBytes((int)length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated symbol/module name.");
        try { return length == 0 ? null : new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Invalid UTF8 symbol/module name.", exception); }
    }
    private static WindowsDumpAnalysisResult Empty(string resourceIdentity, WindowsDumpAnalysisStatus status, string? diagnostic)
        => WindowsDumpAnalysisOwner.Empty(resourceIdentity, status, diagnostic);
}

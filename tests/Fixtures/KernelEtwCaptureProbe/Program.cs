using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

// Finite test recorder. It owns only this freshly named session; no orphan cleanup,
// product services, real-time consumers or process/hardware control are involved.
try
{
    if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--include-interrupts"
        || !OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
        throw new InvalidOperationException("Pass a fresh ETL path, optionally --include-interrupts, and run this finite recorder elevated on Windows.");
    var path = Path.GetFullPath(args[0]);
    if (File.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path)))
        throw new IOException("The ETL must be a new file in an existing evidence directory.");
    using var current = Process.GetCurrentProcess();
    var name = "ResourceManager.OwnedEtlProbe." + current.Id + "." + Guid.NewGuid().ToString("N");
    if (TraceEventSession.GetActiveSessionNames().Contains(name))
        throw new InvalidOperationException("The test session name is already owned.");
    using (var session = new TraceEventSession(name, path) { StopOnDispose = true })
    {
        var keywords = KernelTraceEventParser.Keywords.Process
            | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch;
        if (args.Length == 2)
            keywords |= KernelTraceEventParser.Keywords.Interrupt
                | KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.ImageLoad;
        session.EnableKernelProvider(keywords);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var workers = Enumerable.Range(0, Math.Min(4, Environment.ProcessorCount)).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Thread.SpinWait(4096);
                if (stop.Token.WaitHandle.WaitOne(2)) break;
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20));
        if (!session.Stop(noThrow: false)) throw new IOException("The owned session did not confirm stop.");
    }
    if (TraceEventSession.GetActiveSessionNames().Contains(name))
        throw new IOException("The owned recording session remains active.");
    if (new FileInfo(path).Length == 0) throw new IOException("The owned recording is empty.");
    Console.WriteLine("Owned kernel ETL recording complete; its session was released.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

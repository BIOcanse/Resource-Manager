#Requires -Version 7
# Tray-state disk I/O of the RM processes: a real-time Kernel-File session filtered to their PIDs (no ETL on disk),
# aggregated by process, operation and file, plus per-process IO counter deltas. Run elevated.
# A whole-system kernel file trace is not used: 120 s of it was 8.5 GB on this machine.
# -TraceEventLib defaults to TraceEvent 4.0.0 in the NuGet package cache (NUGET_PACKAGES, else ~/.nuget/packages).
param([Parameter(Mandatory)][string]$OutDir, [int]$Seconds = 120, [int]$Top = 40,
    [string]$TraceEventLib = (Join-Path ($env:NUGET_PACKAGES ? $env:NUGET_PACKAGES : (Join-Path $HOME '.nuget\packages')) 'microsoft.diagnostics.tracing.traceevent\4.0.0\lib\netstandard2.0'))
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run elevated (Start-Process -Verb RunAs): a kernel provider session needs it.'
}
$O = (New-Item -ItemType Directory -Force $OutDir).FullName
$lib = $TraceEventLib
Add-Type -Path (Join-Path $lib 'Microsoft.Diagnostics.FastSerialization.dll')
Add-Type -Path (Join-Path $lib 'Microsoft.Diagnostics.Tracing.TraceEvent.dll')
Add-Type -ReferencedAssemblies @((Join-Path $lib 'Microsoft.Diagnostics.Tracing.TraceEvent.dll'), (Join-Path $lib 'Microsoft.Diagnostics.FastSerialization.dll'), 'netstandard', 'System.Collections', 'System.Runtime', 'System.Linq', 'System.Threading', 'System.Threading.Thread', 'System.Linq.Expressions', 'System.Dynamic.Runtime', 'System.ObjectModel') -TypeDefinition @'
using System; using System.Collections.Generic; using System.Linq; using System.Text; using System.Threading;
using Microsoft.Diagnostics.Tracing; using Microsoft.Diagnostics.Tracing.Session;
public static class LiveFileIo {
    sealed class Row { public long Count; public long Bytes; }
    public static string Run(int[] pids, Dictionary<int, string> names, int seconds, int top, string tsvPath) {
        var rows = new Dictionary<string, Row>();
        var fileNames = new Dictionary<ulong, string>();
        var ops = new Dictionary<int, string> { {12,"create"},{13,"cleanup"},{14,"close"},{15,"read"},{16,"write"},{17,"setinfo"},{18,"setdelete"},{19,"rename"},{20,"direnum"},{21,"flush"},{22,"queryinfo"},{23,"fsctl"},{25,"dirnotify"},{26,"deletepath"},{27,"renamepath"},{28,"setlinkpath"},{30,"createnew"} };
        long total = 0;
        Func<TraceEvent, string, ulong> u = (e, n) => { int i = e.PayloadIndex(n); if (i < 0) return 0; object v = e.PayloadValue(i); try { return Convert.ToUInt64(v); } catch { return 0; } };
        Func<TraceEvent, string, string> s = (e, n) => { int i = e.PayloadIndex(n); return i < 0 ? null : e.PayloadValue(i) as string; };
        var guid = new Guid("EDD08927-9CC4-4E65-B970-C2560FB5C289");
        using (var session = new TraceEventSession("rm-live-fileio-" + Environment.ProcessId)) {
            session.BufferSizeMB = 8;
            var src = session.Source;
            src.Dynamic.All += e => {
                if (e.ProviderGuid != guid) return;
                total++;
                int id = (int)e.ID;
                string fname = s(e, "FileName");
                ulong key = u(e, "FileKey"), obj = u(e, "FileObject");
                if (fname != null) { if (key != 0) fileNames[key] = fname; if (obj != 0) fileNames[obj] = fname; }
                if (id == 10 || id == 11 || id == 24 || id == 32 || id == 36) return;
                if (Array.IndexOf(pids, e.ProcessID) < 0) return;
                string file = fname;
                if (file == null && key != 0) fileNames.TryGetValue(key, out file);
                if (file == null && obj != 0) fileNames.TryGetValue(obj, out file);
                string op; if (!ops.TryGetValue(id, out op)) op = "e" + id;
                string pname; if (!names.TryGetValue(e.ProcessID, out pname)) pname = e.ProcessID.ToString();
                long bytes = (id == 15 || id == 16) ? (long)u(e, "IOSize") : 0;
                string k = pname + "|" + op + "|" + (file ?? "?");
                Row r; if (!rows.TryGetValue(k, out r)) rows[k] = r = new Row();
                r.Count++; r.Bytes += bytes;
            };
            var options = new TraceEventProviderOptions { ProcessIDFilter = pids.ToList() };
            // FILENAME 0x10, FILEIO 0x20, CREATE 0x80, READ 0x100, WRITE 0x200, DELETE_PATH 0x400, RENAME_SETLINK_PATH 0x800, CREATE_NEW_FILE 0x1000
            session.EnableProvider(guid, TraceEventLevel.Verbose, 0x1FB0UL, options);
            var t = new Thread(() => src.Process()) { IsBackground = true }; t.Start();
            session.CaptureState(guid, 0x10UL);
            Thread.Sleep(seconds * 1000);
            session.Stop();
            t.Join(10000);
        }
        var sb = new StringBuilder();
        sb.AppendLine("seconds " + seconds + "  events " + total + "  names " + fileNames.Count);
        var totals = rows.GroupBy(kv => string.Join("|", kv.Key.Split('|').Take(2))).OrderByDescending(g => g.Sum(x => x.Value.Count));
        sb.AppendLine("== totals by process|op");
        foreach (var g in totals) sb.AppendLine(string.Format("{0,9} {1,13:N0}  {2}", g.Sum(x => x.Value.Count), g.Sum(x => x.Value.Bytes), g.Key));
        sb.AppendLine("== by file, count");
        foreach (var kv in rows.OrderByDescending(x => x.Value.Count).Take(top)) sb.AppendLine(string.Format("{0,9} {1,13:N0}  {2}", kv.Value.Count, kv.Value.Bytes, kv.Key));
        sb.AppendLine("== by file, bytes");
        foreach (var kv in rows.Where(x => x.Value.Bytes > 0).OrderByDescending(x => x.Value.Bytes).Take(top)) sb.AppendLine(string.Format("{0,9} {1,13:N0}  {2}", kv.Value.Count, kv.Value.Bytes, kv.Key));
        var tsv = new StringBuilder();
        foreach (var kv in rows) tsv.Append(kv.Value.Count).Append('\t').Append(kv.Value.Bytes).Append('\t').Append(kv.Key.Replace('|', '\t')).Append('\n');
        System.IO.File.WriteAllText(tsvPath, tsv.ToString());
        return sb.ToString();
    }
}
'@
$procs = @(Get-Process -Name 'ResourceManager*')
function Read-IoCounters { foreach ($p in Get-CimInstance Win32_Process | Where-Object { $procs.Id -contains $_.ProcessId }) { [pscustomobject]@{ Id = $p.ProcessId; Name = $p.Name; ReadOps = [int64]$p.ReadOperationCount; ReadBytes = [int64]$p.ReadTransferCount; WriteOps = [int64]$p.WriteOperationCount; WriteBytes = [int64]$p.WriteTransferCount } } }
$before = @(Read-IoCounters)
$names = [Collections.Generic.Dictionary[int,string]]::new(); foreach ($p in $procs) { $names[$p.Id] = $p.ProcessName }
try { [LiveFileIo]::Run([int[]]@($procs.Id), $names, $Seconds, $Top, (Join-Path $O 'live-rows.tsv')) | Out-File (Join-Path $O 'live-summary.txt') -Encoding utf8 }
catch { $_ | Out-String | Out-File (Join-Path $O 'live.err') -Encoding utf8 }
$after = @(Read-IoCounters)
$after | ForEach-Object { $b = $before | Where-Object Id -eq $_.Id | Select-Object -First 1; if ($b) { [pscustomobject]@{ Name = $_.Name; Seconds = $Seconds; ReadOps = $_.ReadOps - $b.ReadOps; ReadBytes = $_.ReadBytes - $b.ReadBytes; WriteOps = $_.WriteOps - $b.WriteOps; WriteBytes = $_.WriteBytes - $b.WriteBytes } } } | ConvertTo-Json | Out-File (Join-Path $O 'io-counters.json') -Encoding utf8

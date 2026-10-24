[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TraceLogPath,
    [Parameter(Mandatory)][string]$TraceEventAssemblyPath,
    [string]$TypePattern = '^(System.IO.FileStream|System.Diagnostics.Process(Module)?|Microsoft.Win32.SafeHandles.Safe(Process|AccessToken)Handle|System.Management.(Management(Base)?Object|IWbemClassObjectFreeThreaded)|RAMSPDToolkit.I2CSMBus.Interop.SMBusData|System.Diagnostics.Tracing.EventSourceAttribute)$'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Read an existing ETLX directly. Do not convert, rewrite, or attach to a process.
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $TraceEventAssemblyPath) 'Microsoft.Diagnostics.FastSerialization.dll'))
[void][Reflection.Assembly]::LoadFrom($TraceEventAssemblyPath)
$traceLog = [Microsoft.Diagnostics.Tracing.Etlx.TraceLog]::new((Resolve-Path -LiteralPath $TraceLogPath).Path)
try {
    $sites = @{}
    foreach ($traceEvent in $traceLog.Events) {
        if ($traceEvent.EventName -ne 'GC/AllocationTick') { continue }
        $typeName = [string]$traceEvent.PayloadByName('TypeName')
        if ($typeName -notmatch $TypePattern) { continue }
        $frames = [Collections.Generic.List[string]]::new()
        $traceStack = [Microsoft.Diagnostics.Tracing.Etlx.TraceLogExtensions]::CallStack($traceEvent)
        $depth = 0
        while ($null -ne $traceStack -and $depth++ -lt 64) {
            $frame = [string]$traceStack.CodeAddress.FullMethodName
            if ($frame.StartsWith('ResourceManager.', [StringComparison]::Ordinal)) {
                $frames.Add($frame)
                if ($frames.Count -eq 3) { break }
            }
            $traceStack = $traceStack.Caller
        }
        $key = $typeName + '|' + ($frames -join '|')
        if (-not $sites.ContainsKey($key)) {
            $sites[$key] = [pscustomobject]@{ Type = $typeName; AllocationTickSamples = 0; Frames = $frames.ToArray() }
        }
        $sites[$key].AllocationTickSamples++
    }
    [pscustomobject]@{
        Trace = $traceLog.FilePath
        Note = 'AllocationTick is sampled allocation evidence, not object counts or a finalization creation stack.'
        Sites = @($sites.Values | Sort-Object Type, AllocationTickSamples -Descending)
    } | ConvertTo-Json -Depth 6
}
finally { $traceLog.Dispose() }

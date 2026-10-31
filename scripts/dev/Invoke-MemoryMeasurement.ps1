# Shared launcher; dot-source from the two public measurement scripts.
function Resolve-MeasurementProcess {
    param([int]$ProcessId, [string]$ProcessName)
    if ($ProcessId -gt 0) { $target = Get-Process -Id $ProcessId -ErrorAction Stop; return $target.Id }
    $targets = @(Get-Process -Name $ProcessName -ErrorAction Stop)
    if ($targets.Count -ne 1) { throw "ProcessName '$ProcessName' matches $($targets.Count) processes; specify -ProcessId." }
    return $targets[0].Id
}

function Invoke-MemoryMeasurement {
    param([string]$ToolPath, [string[]]$ToolArguments, [double]$MemoryLimitGiB)
    if (-not $ToolPath) { $ToolPath = Join-Path $PSScriptRoot 'MemoryMeasurement/bin/Release/net10.0/MemoryMeasurement.dll' }
    if (-not (Test-Path -LiteralPath $ToolPath -PathType Leaf)) {
        throw "Build scripts/dev/MemoryMeasurement/MemoryMeasurement.csproj through JobQueue first, then pass its DLL via -ToolPath. Missing: $ToolPath"
    }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $pwsh = (Get-Process -Id $PID).Path
    $limit = Join-Path $PSScriptRoot 'Invoke-WithMemoryLimit.ps1'
    # ArgumentList is the remaining array argument of Invoke-WithMemoryLimit; direct invocation preserves it.
    # Use a tiny encoded command instead of PowerShell CLI array parsing, which would bind --pid as a parameter.
    $payload = @{ Limit = $limit; Dotnet = $dotnet; Tool = [IO.Path]::GetFullPath($ToolPath); Args = $ToolArguments; GiB = $MemoryLimitGiB } | ConvertTo-Json -Compress
    $encodedPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $command = '$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''' + $encodedPayload + ''')) | ConvertFrom-Json; & $p.Limit -FilePath $p.Dotnet -ArgumentList (@($p.Tool) + @($p.Args)) -LimitGiB $p.GiB; exit $LASTEXITCODE'
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    & $pwsh -NoProfile -EncodedCommand $encodedCommand | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

function Restart-MeasurementElevated {
    param([string]$Script, [System.Collections.IDictionary]$Parameters, [int]$ProcessId)
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Target denied read access even when elevated.' }
    $bound = @{}; foreach ($key in $Parameters.Keys) { if ($key -ne 'ProcessName') { $bound[$key] = $Parameters[$key] } }
    $bound['ProcessId'] = $ProcessId
    $bound['OutDir'] = [IO.Path]::GetFullPath($bound['OutDir'])
    foreach ($key in @('ToolPath', 'ClrMdPath', 'SpeedscopePath', 'RepoRoot')) {
        if ($bound.ContainsKey($key) -and $bound[$key]) { $bound[$key] = [IO.Path]::GetFullPath($bound[$key]) }
    }
    if ($bound.ContainsKey('SeparateZero')) { $bound['SeparateZero'] = [bool]$bound['SeparateZero'] }
    $payload = @{ Script = $Script; Parameters = $bound } | ConvertTo-Json -Depth 6 -Compress
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $command = '$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''' + $b64 + ''')) | ConvertFrom-Json -AsHashtable; & $p.Script @($p.Parameters); if (-not $?) { exit 1 }'
    # Hashtable splatting needs a variable, not an array expression.
    $command = $command.Replace('& $p.Script @($p.Parameters)', '$parameters = $p.Parameters; & $p.Script @parameters')
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute = $true; $info.Verb = 'runas'; $info.WindowStyle = 'Hidden'
    $info.ArgumentList.Add('-NoProfile'); $info.ArgumentList.Add('-EncodedCommand')
    $info.ArgumentList.Add([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))
    $child = [Diagnostics.Process]::Start($info)
    try {
        $child.WaitForExit()
        if ($child.ExitCode -ne 0) { throw "Elevated measurement failed: $($child.ExitCode)" }
        $fileName = if ([IO.Path]::GetFileName($Script) -eq 'Measure-MemoryDuplicates.ps1') { 'summary.txt' } else { 'footprint.txt' }
        Get-Content -LiteralPath (Join-Path $bound['OutDir'] $fileName) | ForEach-Object { Write-Host $_ }
    }
    finally { $child.Dispose() }
}

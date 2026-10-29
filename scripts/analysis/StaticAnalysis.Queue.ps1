function Invoke-StaticAnalysisQueued {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Log,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Repository
    )
    Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
    $queueEntry = Join-Path (Get-MachineRoot -Name ToolsRoot) 'JobQueue\JobQueue.ps1'
    if (-not (Test-Path -LiteralPath $queueEntry)) { throw "Required build queue is unavailable: $queueEntry" }
    & $queueEntry -Action Run -Label $Label -FilePath $FilePath -ArgumentList $Arguments `
        -WorkingDirectory $Repository -MemoryLimitGiB 4 -TimeoutMinutes 120 *> "$Log.queue.log"
    $exitCode = $LASTEXITCODE
    $lastLine = Get-Content -LiteralPath "$Log.queue.log" -Tail 1
    $receipt = $lastLine | ConvertFrom-Json
    if ($null -eq $receipt.PSObject.Properties['status']) { throw "Missing queue receipt: $Log.queue.log" }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$Log.queue.json" -Encoding utf8
    if ($receipt.PSObject.Properties['log'] -and (Test-Path -LiteralPath $receipt.log)) {
        Copy-Item -LiteralPath $receipt.log -Destination $Log
        if (Test-Path -LiteralPath $receipt.errorLog) {
            $errors = Get-Content -LiteralPath $receipt.errorLog -Raw
            if ($errors) { Add-Content -LiteralPath $Log -Value $errors -Encoding utf8 }
        }
    } else { Copy-Item -LiteralPath "$Log.queue.log" -Destination $Log }
    # A memory/timeout failure is terminal for this attempt. No retry or larger
    # ceiling is selected here; the caller records the actual coverage failure.
    return $exitCode
}

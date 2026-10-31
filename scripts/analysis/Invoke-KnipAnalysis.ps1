[CmdletBinding()]
param([Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$repo = [IO.Path]::GetFullPath($RepoRoot)
$output = [IO.Path]::GetFullPath($OutputRoot)
$node = (Get-Command node -CommandType Application | Select-Object -First 1).Source
if (-not (Test-Path -LiteralPath "$repo/src/UI/Frontend/node_modules/solid-js/package.json")) { throw 'Restore frontend dependencies first' }
# Knip 6 parses through oxc-parser raw transfer, which reserves a 4 GiB ArrayBuffer per parser and fails under the
# queue's 4 GiB job ceiling. Knip's own switch turns raw transfer off; it only changes how parse results reach JavaScript.
$env:KNIP_DISABLE_RAW_TRANSFER = '1'
Set-Location -LiteralPath "$repo/src/UI/Frontend"
foreach ($config in @('knip','knip-product')) {
    & $node "$PSScriptRoot/node/node_modules/knip/bin/knip.js" --config "$PSScriptRoot/$config.json" --reporter json --no-progress 1> "$output/$config.json" 2> "$output/$config.log"
    if ($LASTEXITCODE -notin @(0,1)) { throw "Knip execution failed: $LASTEXITCODE" }
    $raw = Get-Content -LiteralPath "$output/$config.json" -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'Knip returned empty output' }
    $parsed = $raw | ConvertFrom-Json
    if ($null -eq $parsed -or $null -eq $parsed.PSObject.Properties['issues']) { throw 'Knip did not return its findings object' }
}

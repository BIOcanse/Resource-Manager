[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RootDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RootDirectory)
$repository = Split-Path -Parent $PSScriptRoot
$jsonHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$trackedJson = @(& git -C $repository ls-files -- '*.json')
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate universal source data.' }
foreach ($relative in $trackedJson) {
    [void]$jsonHashes.Add((Get-FileHash -LiteralPath (Join-Path $repository $relative) -Algorithm SHA256).Hash)
}
$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue($root)
$count = 0
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    foreach ($item in @(Get-Item -LiteralPath $directory) + @(Get-ChildItem -LiteralPath $directory -Force)) {
        if (++$count -gt 100000) { throw 'Unexpected release tree size.' }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in package: $($item.FullName)" }
        if ($item.FullName -eq $directory) { continue }
        if ($item.PSIsContainer) {
            if ($item.Name -in @('UserData', 'Dependencies', 'Misc', '.codex', '.git', 'node_modules', 'obj', 'logs')) {
                throw "Runtime or development directory in package: $($item.FullName)"
            }
            if ($item.Name -eq 'Config' -and @(Get-ChildItem -LiteralPath $item.FullName -Force).Count -ne 0) {
                throw 'Release Config must be empty; user configuration must not be copied.'
            }
            $queue.Enqueue($item.FullName)
        } elseif ($item.Extension -in @('.db', '.sqlite', '.sqlite3', '.log', '.pdb', '.etl', '.trx') -or
                  $item.Name -in @('app-settings.json', 'software-registry.json', 'control-desired-state.json')) {
            throw "Local state or development artifact in package: $($item.FullName)"
        } elseif ($item.Extension -eq '.json' -and $item.Name -notin @('resource-manager-final-image.manifest.json', 'release-manifest.json') -and
                  $item.Name -notlike '*.deps.json' -and $item.Name -notlike '*.runtimeconfig.json') {
            if (-not $jsonHashes.Contains((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)) {
                throw "JSON is not a tracked product default: $($item.FullName)"
            }
        }
    }
}
& (Join-Path $PSScriptRoot 'Test-ResourceManagerFinalImage.ps1') -RootDirectory (Join-Path $root 'Bin') | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $root 'Config') -PathType Container)) { throw 'Missing empty Config directory.' }

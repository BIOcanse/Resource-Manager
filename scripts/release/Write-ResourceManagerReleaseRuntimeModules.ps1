[CmdletBinding()]
param([Parameter(Mandatory)][string]$ImageRoot, [Parameter(Mandatory)][string]$PackageRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$catalog = Get-Content -LiteralPath (Join-Path $ImageRoot 'runtime-modules.json') -Raw | ConvertFrom-Json
if ($catalog.version -ne 1 -or @($catalog.modules).Count -gt 16) { throw 'Invalid composed runtime catalog.' }
foreach ($module in @($catalog.modules)) {
    foreach ($asset in @($module.image, $module.manifest)) {
        if ($null -eq $asset) { continue }
        $relative = [string]$asset.relativePath
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or
            @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) { throw 'Invalid composed runtime asset path.' }
        $asset.relativePath = 'Bin/' + $relative
    }
}
[IO.File]::WriteAllText((Join-Path $PackageRoot 'runtime-modules.json'), (($catalog | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))

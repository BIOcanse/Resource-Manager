#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ReleaseOutputPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
        # Generations root, never the version directory. Parameter > environment > checked-in setting.
        [string]$OutputRoot,
        [string]$SettingsPath = (Join-Path $PSScriptRoot 'release-output-settings.json')
    )
    $settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($settings.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($settings.generationRoot)) { throw 'Invalid release output settings.' }
    $selected = $OutputRoot
    if ([string]::IsNullOrWhiteSpace($selected)) { $selected = $env:RM_RELEASE_OUTPUT_ROOT }
    if ([string]::IsNullOrWhiteSpace($selected)) { $selected = [string]$settings.generationRoot }
    if (-not [IO.Path]::IsPathRooted($selected)) { $selected = Join-Path $RepositoryRoot $selected }
    $root = [IO.Path]::GetFullPath($selected).TrimEnd('\', '/')
    if ($root -ieq [IO.Path]::GetPathRoot($root).TrimEnd('\', '/')) { throw 'Release output root cannot be a volume root.' }
    $directory = Join-Path $root $Version
    $ancestor = $directory
    $depth = 0
    while (-not [string]::IsNullOrWhiteSpace($ancestor)) {
        if (++$depth -gt 128) { throw 'Release output path exceeds its component bound.' }
        if ([IO.File]::Exists($ancestor)) { throw 'Release output path is a file.' }
        if ([IO.Directory]::Exists($ancestor) -and
            ([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release output path contains a reparse point.' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    [pscustomobject]@{ GenerationsRoot = $root; GenerationDirectory = $directory }
}
Export-ModuleMember -Function Get-ReleaseOutputPaths

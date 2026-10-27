#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TreeJson)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Get-Content -LiteralPath $TreeJson -Raw | ConvertFrom-Json -Depth 100
if ($source.schemaVersion -ne 1 -or $source.process -cne 'desktop' -or $source.program -ne 40) {
    throw 'Expected the actual resident Program root (desktop UID 40).'
}
$roots = @($source.blocks | Where-Object { $null -eq $_.parent })
if ($roots.Count -ne 1 -or $roots[0].uid -ne $source.program) { throw 'The export must start at the resident root.' }
$frontend = @($source.blocks | Where-Object { $_.parent -eq $source.program })
if ($frontend.Count -ne 1 -or $frontend[0].uid -ne 101 -or $frontend[0].name -cne 'Frontend') {
    throw 'The root must export its single Frontend branch with the renumbered UID.'
}
$window = @($source.blocks | Where-Object name -CEQ 'WindowProcess')
if ($window.Count -ne 1 -or $window[0].uid -ne 89 -or $window[0].parent -ne 41 -or
    $window[0].declarations.Count -ne 1 -or $window[0].declarations[0].launchesProcess -cne 'window') {
    throw 'WindowProcess must export the window launch under MainFrontend with the renumbered UID.'
}
$launchers = @($source.blocks | Where-Object { @($_.declarations | Where-Object { $null -ne $_.launchesProcess }).Count -gt 0 })
if ($launchers.Count -ne 1 -or $launchers[0].uid -ne $window[0].uid) { throw 'Only WindowProcess launches a process.' }
'Resident control tree export: root, Frontend and the unique window process launch passed.'

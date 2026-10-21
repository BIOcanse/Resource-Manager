[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$GroupId,
    [Parameter(Mandatory)][string]$CompileManifestPath,
    [Parameter(Mandatory)][string]$BuildContextPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-FileHash([string]$Path,[string]$Expected) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or -not [IO.File]::Exists($Path) -or
        (Get-FileHash -LiteralPath $Path).Hash -ine $Expected) { throw "Runtime selection input changed: $Path" }
}
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
if ((Get-Item -LiteralPath $manifestFile).Length -gt 4MB) { throw 'Runtime manifest exceeds its bound.' }
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$build = Get-Content -LiteralPath $BuildContextPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.kind -cne 'AppTestRuntimePartitions' -or
    -not $build.buildSucceeded -or $manifest.partitionId -cne $build.partitionId -or
    [IO.Path]::GetFullPath($manifest.testCompileManifest) -ine [IO.Path]::GetFullPath($CompileManifestPath) -or
    [IO.Path]::GetFullPath($manifest.buildRunDirectory) -ine [IO.Path]::GetFullPath($build.runDirectory) -or
    $manifest.testAssemblySha256 -ine $build.testAssemblySha256 -or
    $manifest.testCompileManifestSha256 -ine $build.testCompileManifestSha256) { throw 'Runtime selection must bind the latest exact successful compile partition.' }
Assert-FileHash $manifest.testCompileManifest $manifest.testCompileManifestSha256
Assert-FileHash $manifest.planPath $manifest.planSha256
Assert-FileHash (Join-Path $manifest.buildRunDirectory 'partition-build.json') $manifest.partitionBuildSha256
Assert-FileHash $manifest.assemblyArchive $manifest.testAssemblySha256
Assert-FileHash $build.testAssemblyPath $manifest.testAssemblySha256
$receipt = Get-Content -LiteralPath (Join-Path $build.runDirectory 'bounded-result.json') -Raw | ConvertFrom-Json
if (-not $receipt.accepted -or -not $receipt.normalDrain -or $receipt.stage -cne 'Build') { throw 'Runtime selection requires an accepted bounded Build.' }
if (@($manifest.tools).Count -eq 0) { throw 'Runtime selection has no generator tool identity.' }
foreach ($tool in $manifest.tools) { Assert-FileHash $tool.path $tool.sha256 }
$all = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$groupIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$selected = $null
if (@($manifest.groups).Count -eq 0 -or @($manifest.groups).Count -gt 256) { throw 'Runtime group count is out of bounds.' }
foreach ($group in $manifest.groups) {
    if (-not $groupIds.Add([string]$group.groupId) -or @($group.methods).Count -eq 0 -or
        [string]::IsNullOrWhiteSpace([string]$group.filter) -or $group.filter.Length -gt 131072) { throw 'Runtime groups require unique IDs, methods and a bounded exact filter.' }
    foreach ($method in $group.methods) { if (-not $all.Add([string]$method)) { throw "Runtime method belongs to multiple groups: $method" } }
    if ($group.groupId -ceq $GroupId) { $selected = $group }
}
$declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($method in $manifest.methods) { if (-not $declared.Add([string]$method)) { throw 'Runtime metadata method list has duplicates.' } }
if (-not $all.SetEquals($declared) -or $null -eq $selected) { throw 'Runtime groups must cover exactly the declared metadata methods and include the selected group.' }
[pscustomobject]@{
    schemaVersion = 1; partitionId = [string]$manifest.partitionId
    runtimeManifestPath = $manifestFile; runtimeManifestSha256 = (Get-FileHash -LiteralPath $manifestFile).Hash
    groupId = $GroupId; filter = [string]$selected.filter; testAssemblySha256 = [string]$manifest.testAssemblySha256
}

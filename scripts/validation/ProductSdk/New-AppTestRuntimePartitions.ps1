#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanPath,
    [Parameter(Mandatory)][string]$BuildRunDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1,4096)][int]$MaxMethodsPerGroup = 128,
    [string[]]$IsolateMethods = @(),
    [string]$SelectedMethodsPath,
    [string]$MetadataLoadContextPath = 'C:\Program Files\dotnet\sdk\10.0.300\System.Reflection.MetadataLoadContext.dll'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
# Reuse the exact metadata interpretation that the final coverage checker uses.
# FunctionsOnly returns before reading any receipt or writing output.
$coverageTool=Join-Path $PSScriptRoot 'Test-AppTestPartitionCoverage.ps1'
$generatorTool=$PSCommandPath
. $coverageTool -PlanPath $PlanPath -OutputPath $OutputPath -MetadataLoadContextPath $MetadataLoadContextPath -FunctionsOnly
$planFile=[IO.Path]::GetFullPath($PlanPath);$plan=Read-Json $planFile
$buildRun=[IO.Path]::GetFullPath($BuildRunDirectory)
[void](Receipt $buildRun 'Build')
$buildFile=Join-Path $buildRun 'partition-build.json';$build=Read-Json $buildFile
Check ($build.buildSucceeded) 'Runtime grouping requires a successful accepted partition Build.'
$entry=@($plan.partitions|Where-Object partitionId -CEQ $build.partitionId)
Check ($entry.Count -eq 1 -and $build.testProject -ieq $plan.projectPath) 'Build does not belong to this compile plan.'
$manifestPath=[string]$entry[0].manifestPath;$manifestHash=Hash $manifestPath
Check ($manifestHash -ieq $build.testCompileManifestSha256) 'Build compile manifest changed.'
Verify-Hash (Join-Path $buildRun 'test-compile-manifest.json') $manifestHash
$outputs=@(Read-Json (Join-Path $buildRun 'test-output-build.json'))
$dll=@($outputs|Where-Object {$_.exists -and $_.path -ieq $build.testAssemblyPath})
Check ($dll.Count -eq 1 -and $dll[0].sha256 -ieq $build.testAssemblySha256) 'Build archive does not identify exactly one test DLL.'
Verify-Hash $dll[0].archive $build.testAssemblySha256
$references=Lines (Join-Path $buildRun 'compile-partition/references.txt')
$metadata=Inspect-Metadata $dll[0].archive ([IO.Path]::GetDirectoryName($build.testAssemblyPath)) $references
Check ($metadata.openGenericClasses.Count -eq 0) 'Open generic test classes need explicit discovery reconciliation.'
$methodRows=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($method in $metadata.discoverable) {
    if(-not $methodRows.ContainsKey($method.key)){$methodRows[$method.key]=[Collections.Generic.List[object]]::new()}
    $methodRows[$method.key].Add($method)
}
$allMethods=New-Set @($methodRows.Keys) -Ordinal
Check ($allMethods.Count -gt 0 -and $allMethods.Count -le 32768) 'Discoverable method count is outside its bound.'
$focused=$false;$selectedMethods=$allMethods;$selectedInputPath=$null;$selectedMethodsHash=$null
if($SelectedMethodsPath){
    $focused=$true;$selectedInputPath=[IO.Path]::GetFullPath($SelectedMethodsPath)
    Check ([IO.File]::Exists($selectedInputPath)) "SelectedMethodsPath does not exist: $selectedInputPath"
    $selectedMethodsHash=Hash $selectedInputPath
    $decoded=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $selectedInputPath -Raw) -NoEnumerate
    Check ($decoded -is [array] -and $decoded.Count -gt 0) 'SelectedMethodsPath must contain a nonempty JSON string array.'
    $selectedMethods=New-Set @() -Ordinal
    foreach($method in $decoded){
        Check ($method -is [string] -and -not [string]::IsNullOrWhiteSpace($method) -and $method -ceq $method.Trim()) 'SelectedMethodsPath entries must be exact, nonempty runtime method keys.'
        Check ($selectedMethods.Add($method)) "SelectedMethodsPath contains a duplicate method: $method"
        Check ($allMethods.Contains($method)) "SelectedMethodsPath contains a non-discoverable method: $method"
    }
}
$isolated=New-Set @($IsolateMethods) -Ordinal
Check ($isolated.IsSubsetOf($allMethods)) 'Every IsolateMethods entry must be an exact discoverable FQN.'
if($focused){$isolated.IntersectWith($selectedMethods)}
$groups=[Collections.Generic.List[object]]::new();$pending=[Collections.Generic.List[string]]::new()
foreach($key in @($isolated|Sort-Object)){$groups.Add([pscustomobject]@{groupId='';methods=@($key);filter='';isolated=$true})}
foreach($key in @($selectedMethods|Sort-Object)) {
    if($isolated.Contains($key)){continue}
    $pending.Add($key)
    if($pending.Count -eq $MaxMethodsPerGroup){$groups.Add([pscustomobject]@{groupId='';methods=@($pending.ToArray());filter='';isolated=$false});$pending.Clear()}
}
if($pending.Count){$groups.Add([pscustomobject]@{groupId='';methods=@($pending.ToArray());filter='';isolated=$false})}
Check ($groups.Count -le 256) 'Runtime grouping exceeds 256 groups; increase MaxMethodsPerGroup.'
$covered=New-Set @() -Ordinal;$ordinal=0
foreach($group in $groups) {
    $group.groupId='run-'+(++$ordinal).ToString('000')
    $selectors=New-Set @() -Ordinal
    foreach($key in $group.methods) {
        Check ($covered.Add($key)) "Method assigned to more than one runtime group: $key"
        foreach($row in $methodRows[$key]) {
            # Nested type names differ in reflection and adapter display. Exact
            # aliases are kept in the same OR group and never split data cases.
            foreach($name in @($key,($row.className+'.'+$row.method))) {
                Check ($name -match '^[\p{L}\p{N}_.$+`]+$') "FQN requires unsupported VSTest filter escaping: $name"
                [void]$selectors.Add('FullyQualifiedName='+$name)
            }
        }
    }
    $group.filter=[string]::Join('|',[string[]]@($selectors|Sort-Object))
    Check ($group.filter.Length -le 131072) 'Runtime filter exceeds 128 KiB.'
}
Check ($covered.SetEquals($selectedMethods)) 'Runtime groups omitted declared methods.'
if($focused){Verify-Hash $selectedInputPath $selectedMethodsHash}
$toolPaths=@($generatorTool,$coverageTool,$MetadataLoadContextPath)
$result=[ordered]@{
    schemaVersion=1;kind='AppTestRuntimePartitions';partitionId=$build.partitionId
    planPath=$planFile;planSha256=(Hash $planFile)
    testCompileManifest=$manifestPath;testCompileManifestSha256=$manifestHash
    buildRunDirectory=$buildRun;partitionBuildSha256=(Hash $buildFile)
    testAssemblySha256=$build.testAssemblySha256;assemblyArchive=$dll[0].archive;assemblyName=$metadata.assemblyName
    maxMethodsPerGroup=$MaxMethodsPerGroup;isolatedMethods=@($isolated|Sort-Object)
    focused=$focused;wholeAssemblySelected=(-not $focused);discoveredMethodCount=$allMethods.Count
    discoveryScope='Built test assembly metadata; methods contains the selected declaration set.'
    selectedMethodsPath=$selectedInputPath;selectedMethodsSha256=$selectedMethodsHash
    tools=@($toolPaths|ForEach-Object {[pscustomobject]@{path=[IO.Path]::GetFullPath($_);sha256=(Hash $_)}})
    methods=@($selectedMethods|Sort-Object);groups=@($groups.ToArray())
    metadataDeclarationCount=$metadata.declared.Count;metadataDiscoverableDeclarationCount=$metadata.discoverable.Count
    methodMapping=@(foreach($key in @($selectedMethods|Sort-Object)){foreach($row in $methodRows[$key]){$row}})
}
$destination=[IO.Path]::GetFullPath($OutputPath)
Check ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($destination))) 'Output parent must already exist in a registered task.'
Check (-not [IO.File]::Exists($destination)) 'Runtime manifest is immutable; select a new output path.'
[IO.File]::WriteAllText($destination,($result|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output (@{partitionId=$build.partitionId;methodCount=$selectedMethods.Count;discoveredMethodCount=$allMethods.Count;focused=$focused;groupCount=$groups.Count;outputPath=$destination;sha256=(Hash $destination)}|ConvertTo-Json -Compress)

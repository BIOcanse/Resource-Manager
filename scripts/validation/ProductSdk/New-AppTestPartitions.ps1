[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputManifestPath,
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,32)][int]$PartitionCount=6,
    [ValidateRange(262144,67108864)][long]$MaximumPartitionSourceBytes=1887436,
    [ValidateSet('All','DependencyClosure')][string]$SharedSourceMode='All',
    [string]$PreservePlanPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath($ProjectPath)
$inputPath=[IO.Path]::GetFullPath($InputManifestPath)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not [IO.File]::Exists($project)){throw 'Original test project is missing.'}
$metadata=Get-Content -LiteralPath $inputPath -Raw|ConvertFrom-Json
$items=@($metadata.Items.Compile)
if($items.Count -eq 0 -or $items.Count -gt 4096){throw 'Compile metadata is empty or exceeds the source bound.'}
# Use the PowerShell runtime's already compatible Roslyn, only for syntax parsing.
# This does not create a Compilation, run analyzers, or invoke a compiler.
Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp
$utf8=[Text.UTF8Encoding]::new($false)
function Write-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 12),$utf8)}
function Type-Key($Node) {
    $parts=[Collections.Generic.List[string]]::new()
    $cursor=$Node.Parent
    while($null -ne $cursor) {
        if($cursor -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax]){$parts.Insert(0,$cursor.Name.ToString())}
        $cursor=$cursor.Parent
    }
    $name=$Node.Identifier.ValueText
    if($Node.PSObject.Properties.Name -contains 'TypeParameterList' -and $null -ne $Node.TypeParameterList){$name+='`'+$Node.TypeParameterList.Parameters.Count}
    return ($parts -join '.')+'|'+$name
}
function Root-Type($Node) {
    $root=$null;$cursor=$Node.Parent
    while($null -ne $cursor) {
        if($cursor -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax]){$root=$cursor}
        $cursor=$cursor.Parent
    }
    return $root
}
function Short-Name([string]$Name){return ($Name.Split('.')[-1] -replace 'Attribute$','')}
$paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$rows=[Collections.Generic.List[object]]::new()
$attributeBases=[Collections.Generic.List[object]]::new()
$totalBytes=0L
foreach($item in $items) {
    $path=[string]$item.FullPath
    if(-not [IO.Path]::IsPathRooted($path) -or -not [IO.File]::Exists($path) -or -not $paths.Add($path)){throw 'Compile items must have unique existing absolute FullPath values.'}
    $length=([IO.FileInfo]$path).Length;$totalBytes+=$length
    if($totalBytes -gt 67108864){throw 'Compile sources exceed the 64 MiB parsing bound.'}
    $text=[IO.File]::ReadAllText($path)
    $root=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text).GetRoot()
    $types=[Collections.Generic.List[object]]::new()
    $references=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $methods=[Collections.Generic.List[object]]::new()
    $collectionDefinitions=[Collections.Generic.List[string]]::new();$collectionUses=[Collections.Generic.List[string]]::new()
    foreach($node in $root.DescendantNodes()) {
        if($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax] -and $null -ne $node.ArgumentList -and $node.ArgumentList.Arguments.Count -gt 0){
            $attribute=Short-Name $node.Name.ToString();$value=$node.ArgumentList.Arguments[0].Expression
            if($value -is [Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax] -and $value.Token.Value -is [string]){
                if($attribute -ceq 'CollectionDefinition'){$collectionDefinitions.Add($value.Token.ValueText)}
                if($attribute -ceq 'Collection'){$collectionUses.Add($value.Token.ValueText)}
            }
        }
        if($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax] -or $node -is [Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax]){[void]$references.Add($node.Identifier.ValueText)}
        if($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax] -or $node -is [Microsoft.CodeAnalysis.CSharp.Syntax.DelegateDeclarationSyntax]) {
            if($node.Parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax] -or $node.Parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax]){$types.Add([pscustomobject]@{key=(Type-Key $node);name=$node.Identifier.ValueText})}
            if($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] -and $null -ne $node.BaseList){$attributeBases.Add([pscustomobject]@{name=(Short-Name $node.Identifier.ValueText);bases=@($node.BaseList.Types|ForEach-Object {Short-Name $_.Type.ToString()})})}
        }
        if($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $node.AttributeLists.Count -gt 0) {
            $owner=Root-Type $node
            if($null -ne $owner){$generic=if($null -eq $node.TypeParameterList){''}else{$node.TypeParameterList.ToString()};$methods.Add([pscustomobject]@{type=(Type-Key $owner);method=($node.Identifier.ValueText+$generic+$node.ParameterList.ToString());attributes=@($node.AttributeLists.Attributes|ForEach-Object {Short-Name $_.Name.ToString()})})}
        }
    }
    $rows.Add([pscustomobject]@{path=$path;bytes=$length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;types=$types;references=$references;methods=$methods;collectionDefinitions=$collectionDefinitions;collectionUses=$collectionUses;tests=@();dependencies=[Collections.Generic.HashSet[int]]::new();bound=$false})
}
$attributeNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[void]$attributeNames.Add('Fact');[void]$attributeNames.Add('Theory')
for($iteration=0;$iteration -le $attributeBases.Count;$iteration++) {
    $changed=$false
    foreach($definition in $attributeBases){foreach($base in $definition.bases){if($attributeNames.Contains($base) -and $attributeNames.Add($definition.name)){$changed=$true}}}
    if(-not $changed){break}
}
$testTypes=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$declarations=[Collections.Generic.Dictionary[string,Collections.Generic.List[int]]]::new([StringComparer]::Ordinal)
for($index=0;$index -lt $rows.Count;$index++) {
    $row=$rows[$index]
    $row.tests=@($row.methods|Where-Object {$method=$_;@($method.attributes|Where-Object {$attributeNames.Contains($_)}).Count -gt 0}|ForEach-Object {$_.type+'::'+$_.method})
    foreach($method in $row.methods){if(@($method.attributes|Where-Object {$attributeNames.Contains($_)}).Count -gt 0){[void]$testTypes.Add($method.type)}}
    foreach($type in $row.types) {
        foreach($name in @($type.name,($type.name -replace 'Attribute$',''))|Select-Object -Unique) {
            if(-not $declarations.ContainsKey($name)){$declarations[$name]=[Collections.Generic.List[int]]::new()}
            $declarations[$name].Add($index)
        }
    }
}
for($index=0;$index -lt $rows.Count;$index++) {
    $row=$rows[$index]
    foreach($type in $row.types){if($testTypes.Contains($type.key)){$row.bound=$true};[void]$row.references.Add($type.name)}
    foreach($name in $row.references){if($declarations.ContainsKey($name)){foreach($other in $declarations[$name]){[void]$row.dependencies.Add($other)}}}
}
# Literal xUnit collection names also form dependencies. A definition may share a file with tests.
if($SharedSourceMode -ceq 'DependencyClosure'){
    $definitions=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    for($index=0;$index -lt $rows.Count;$index++){foreach($name in $rows[$index].collectionDefinitions){if($definitions.ContainsKey($name)){throw 'Duplicate named xUnit collection definition.'};$definitions.Add($name,$index)}}
    foreach($row in $rows){foreach($name in $row.collectionUses){if($definitions.ContainsKey($name)){[void]$row.dependencies.Add($definitions[$name])}}}
}
# A shared helper must not depend on a test type, including indirect helper chains.
for($iteration=0;$iteration -le $rows.Count;$iteration++) {
    $changed=$false
    foreach($row in $rows){if($row.bound){continue};foreach($other in $row.dependencies){if($rows[$other].bound){$row.bound=$true;$changed=$true;break}}}
    if(-not $changed){break}
}
$shared=@($rows|Where-Object {-not $_.bound}|Sort-Object path)
foreach($required in @('NativeTestProcessStartup.cs','TestRepositoryPaths.cs')) {
    if(@($rows|Where-Object {[IO.Path]::GetFileName($_.path) -ceq $required}).Count -eq 1 -and @($shared|Where-Object {[IO.Path]::GetFileName($_.path) -ceq $required}).Count -ne 1){throw "Required shared initialization source has a test dependency: $required"}
}
$edges=@{}
for($index=0;$index -lt $rows.Count;$index++){if($rows[$index].bound){$edges[$index]=[Collections.Generic.HashSet[int]]::new()}}
foreach($index in @($edges.Keys)){foreach($other in $rows[$index].dependencies){if($rows[$other].bound){[void]$edges[$index].Add($other);[void]$edges[$other].Add($index)}}}
$seen=[Collections.Generic.HashSet[int]]::new();$components=[Collections.Generic.List[object]]::new()
foreach($index in @($edges.Keys|Sort-Object)) {
    if(-not $seen.Add($index)){continue}
    $queue=[Collections.Generic.Queue[int]]::new();$queue.Enqueue($index);$members=[Collections.Generic.List[int]]::new()
    while($queue.Count -gt 0){$member=$queue.Dequeue();$members.Add($member);foreach($other in $edges[$member]){if($seen.Add($other)){$queue.Enqueue($other)}}}
    $memberRows=@($members|ForEach-Object {$rows[$_]}|Sort-Object path)
    $components.Add([pscustomobject]@{key=$memberRows[0].path;bytes=($memberRows|Measure-Object bytes -Sum).Sum;rows=$memberRows})
}
$sharedBytes=($shared|Measure-Object bytes -Sum).Sum
if($null -eq $sharedBytes){$sharedBytes=0L}
$buckets=@(for($index=0;$index -lt $PartitionCount;$index++){[pscustomobject]@{index=$index;bytes=[long]$sharedBytes;rows=[Collections.Generic.List[object]]::new()}})
$preservedAssignment=$null
$dependencyScoped=$SharedSourceMode -ceq 'DependencyClosure'
$alwaysShared=@($shared|Where-Object {
    $syntax=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_.path)).GetRoot()
    # Extension calls do not necessarily name their declaring type. Keep their files global.
    $_.types.Count -eq 0 -or $syntax.AttributeLists.Count -gt 0 -or
    @($syntax.Usings|Where-Object {$_.GlobalKeyword.RawKind -ne 0}).Count -gt 0 -or
    @($syntax.DescendantNodes()|Where-Object {
        ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax] -and (Short-Name $_.Name.ToString()) -in @('ModuleInitializer','CollectionDefinition')) -or
        ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax] -and @($_.Modifiers|Where-Object Text -ceq 'this').Count -gt 0)
    }).Count -gt 0
})
function Shared-Closure($MemberRows) {
    $selected=[Collections.Generic.HashSet[int]]::new()
    $queue=[Collections.Generic.Queue[int]]::new()
    foreach($row in $alwaysShared){$index=$rows.IndexOf($row);if($selected.Add($index)){$queue.Enqueue($index)}}
    foreach($row in @($MemberRows)) {
        foreach($other in $row.dependencies){if(-not $rows[$other].bound -and $selected.Add($other)){$queue.Enqueue($other)}}
    }
    while($queue.Count -gt 0){foreach($other in $rows[$queue.Dequeue()].dependencies){if(-not $rows[$other].bound -and $selected.Add($other)){$queue.Enqueue($other)}}}
    return @($selected|ForEach-Object {$rows[$_]}|Sort-Object path)
}
if($dependencyScoped){foreach($bucket in $buckets){$bucket.bytes=0L}}

if(-not [string]::IsNullOrWhiteSpace($PreservePlanPath)) {
    $preservePath=[IO.Path]::GetFullPath($PreservePlanPath)
    $prior=Get-Content -LiteralPath $preservePath -Raw|ConvertFrom-Json
    if($prior.schemaVersion -ne 1 -or [string]$prior.projectPath -ine $project -or @($prior.partitions).Count -ne $PartitionCount){throw 'Preserved plan must select the same project and partition count.'}
    if($MaximumPartitionSourceBytes -gt [long]$prior.maximumPartitionSourceBytes){throw 'Preserving assignments cannot increase the source-byte cap.'}
    $priorPaths=[Collections.Generic.HashSet[string]]::new([string[]]@($prior.originalSourceFiles),[StringComparer]::OrdinalIgnoreCase)
    $priorShared=[Collections.Generic.HashSet[string]]::new([string[]]@($prior.sharedSourceFiles),[StringComparer]::OrdinalIgnoreCase)
    if(-not $priorPaths.SetEquals($paths) -or -not $priorShared.SetEquals([string[]]@($shared|ForEach-Object path))){throw 'Preserved Compile and shared-source sets must equal the recomputed sets; create an explicit new plan for changed membership.'}
    $assignment=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::OrdinalIgnoreCase)
    for($index=0;$index -lt $PartitionCount;$index++) {
        $expectedId='app-'+($index+1).ToString('00')
        $priorPartition=@($prior.partitions|Where-Object partitionId -CEQ $expectedId)
        if($priorPartition.Count -ne 1){throw "Preserved partition is missing or duplicated: $expectedId"}
        $priorManifest=Get-Content -LiteralPath $priorPartition[0].manifestPath -Raw|ConvertFrom-Json
        if($priorManifest.partitionId -cne $expectedId -or [string]$priorManifest.projectPath -ine $project){throw 'Preserved manifest identity differs from its plan.'}
        $manifestPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($path in $priorManifest.sourceFiles) {
            if(-not $paths.Contains([string]$path) -or -not $manifestPaths.Add([string]$path)){throw 'Preserved manifest has an unknown or duplicate Compile source.'}
            if(-not $priorShared.Contains([string]$path)) {
                if($assignment.ContainsKey([string]$path)){throw 'Preserved nonshared Compile belongs to multiple partitions.'}
                $assignment.Add([string]$path,$index)
            }
        }
        $localShared=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($path in @($priorManifest.sharedSourceFiles)) {
            if(-not $priorShared.Contains([string]$path) -or -not $manifestPaths.Contains([string]$path) -or -not $localShared.Add([string]$path)){throw 'Preserved partition has an inconsistent shared-source map.'}
        }
        if(@($manifestPaths|Where-Object {$priorShared.Contains($_)}).Count -ne $localShared.Count){throw 'Preserved partition omitted a declared shared-source mapping.'}
    }
    foreach($component in $components) {
        $indices=[Collections.Generic.HashSet[int]]::new()
        foreach($row in $component.rows) {
            if(-not $assignment.ContainsKey($row.path)){throw 'Preserved plan omitted a nonshared Compile source.'}
            [void]$indices.Add($assignment[$row.path])
        }
        if($indices.Count -ne 1){throw "A recomputed partial/type dependency component crosses preserved partitions: $($component.key)"}
        $bucket=$buckets[@($indices)[0]]
        $bucket.bytes+=$component.bytes
        if($bucket.bytes -gt $MaximumPartitionSourceBytes){throw 'A preserved partition exceeds the recomputed source-byte cap; no assignments were moved.'}
        foreach($row in $component.rows){$bucket.rows.Add($row)}
    }
    $preservedAssignment=@{sourcePlanPath=$preservePath;sourcePlanSha256BeforeRefresh=(Get-FileHash -LiteralPath $preservePath -Algorithm SHA256).Hash;sourceSetsUnchanged=$true;partialAndTypeDependencyComponentsRetained=$true;assignmentsMoved=0}
} else {
    foreach($component in $components|Sort-Object @{Expression='bytes';Descending=$true},key) {
        if($dependencyScoped){
            $candidates=@(foreach($candidate in $buckets){
                $members=@($candidate.rows)+@($component.rows);$helpers=@(Shared-Closure $members)
                $bytes=[long](($members|Measure-Object bytes -Sum).Sum)+[long](($helpers|Measure-Object bytes -Sum).Sum)
                if($bytes -le $MaximumPartitionSourceBytes){[pscustomobject]@{bucket=$candidate;bytes=$bytes;index=$candidate.index}}
            })
            $choice=@($candidates|Sort-Object bytes,index|Select-Object -First 1)
            if($choice.Count -ne 1){throw "No partition fits indivisible component and its dependency closure $($component.key); no sources were omitted."}
            $bucket=@($choice[0].bucket);$bucket[0].bytes=$choice[0].bytes
        }else{
            $bucket=@($buckets|Where-Object {$_.bytes+$component.bytes -le $MaximumPartitionSourceBytes}|Sort-Object bytes,index|Select-Object -First 1)
            if($bucket.Count -ne 1){throw "No partition fits indivisible component $($component.key) ($($component.bytes) bytes); no sources were omitted."}
            $bucket[0].bytes+=$component.bytes
        }
        foreach($row in $component.rows){$bucket[0].rows.Add($row)}
    }
}
$bucketShared=@{}
if($dependencyScoped){
    $included=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($bucket in $buckets){$helpers=@(Shared-Closure @($bucket.rows));$bucketShared[$bucket.index]=[Collections.Generic.List[object]]::new();foreach($row in $helpers){$bucketShared[$bucket.index].Add($row);[void]$included.Add($row.path)}}
    if(-not [string]::IsNullOrWhiteSpace($PreservePlanPath)) {
        foreach($bucket in $buckets) {
            $bucket.bytes=[long](($bucket.rows|Measure-Object bytes -Sum).Sum)+[long](($bucketShared[$bucket.index]|Measure-Object bytes -Sum).Sum)
            if($bucket.bytes -gt $MaximumPartitionSourceBytes){throw 'A preserved partition and its dependency closure exceed the source-byte cap; no sources were omitted.'}
        }
    }
    # Retain unused helpers once, so the actual compiler union still covers every original Compile item.
    foreach($row in $shared){if(-not $included.Contains($row.path)){
        # Include its own helper dependencies too; it may contain a non-test helper chain.
        $needed=@($row)+@(Shared-Closure @($row))
        $choices=@(foreach($candidate in $buckets){
            $present=[Collections.Generic.HashSet[string]]::new([string[]]@($bucketShared[$candidate.index]|ForEach-Object path),[StringComparer]::OrdinalIgnoreCase)
            $extra=0L;foreach($helper in $needed){if($present.Add($helper.path)){$extra+=$helper.bytes}}
            if($candidate.bytes+$extra -le $MaximumPartitionSourceBytes){[pscustomobject]@{bucket=$candidate;bytes=($candidate.bytes+$extra);index=$candidate.index}}
        })
        $choice=@($choices|Sort-Object bytes,index|Select-Object -First 1)
        if($choice.Count -ne 1){throw 'An original unused helper dependency closure cannot fit; no sources were omitted.'}
        $target=$choice[0].bucket;$existing=[Collections.Generic.HashSet[string]]::new([string[]]@($bucketShared[$target.index]|ForEach-Object path),[StringComparer]::OrdinalIgnoreCase)
        foreach($helper in $needed){if($existing.Add($helper.path)){$target.bytes+=$helper.bytes;$bucketShared[$target.index].Add($helper);[void]$included.Add($helper.path)}}
        if($target.bytes -gt $MaximumPartitionSourceBytes){throw 'Original unused helper dependency closure exceeds cap; no sources were omitted.'}
    }}
}else{foreach($bucket in $buckets){$bucketShared[$bucket.index]=@($shared)}}
$coverage=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::OrdinalIgnoreCase)
$testCoverage=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($bucket in $buckets){foreach($row in $bucket.rows){if($coverage.ContainsKey($row.path)){throw 'Nonshared Compile appears in multiple partitions.'};$coverage.Add($row.path,1);foreach($test in $row.tests){if(-not $testCoverage.Add($test)){throw "Duplicate test declaration: $test"}}}}
foreach($row in $shared){if($row.tests.Count -ne 0){throw 'A test declaration cannot be shared.'};$coverage.Add($row.path,1)}
if($coverage.Count -ne $paths.Count -or @($paths|Where-Object {-not $coverage.ContainsKey($_)}).Count -gt 0){throw 'Partition union does not equal original Compile items.'}
[void][IO.Directory]::CreateDirectory($output)
$inputIdentity=[ordered]@{sha256=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash;path=$inputPath}
$manifests=[Collections.Generic.List[object]]::new()
foreach($bucket in $buckets) {
    if($bucket.rows.Count -eq 0){throw 'An empty test partition is not accepted.'}
    $id='app-'+($bucket.index+1).ToString('00')
    $directory=Join-Path $output $id;[void][IO.Directory]::CreateDirectory($directory)
    $localShared=@($bucketShared[$bucket.index]|Sort-Object path)
    $sourceFiles=@(@($localShared)+@($bucket.rows)|ForEach-Object path|Sort-Object)
    $listPath=Join-Path $directory 'sourceList.txt'
    [IO.File]::WriteAllLines($listPath,[string[]]$sourceFiles,$utf8)
    $manifest=[ordered]@{schemaVersion=1;partitionId=$id;projectPath=$project;sourceFiles=$sourceFiles;sharedSourceFiles=@($localShared|ForEach-Object path);sourceListPath=$listPath;originalCompileMetadata=$inputIdentity;testDeclarations=@($bucket.rows|ForEach-Object tests|Sort-Object);sourceBytes=$bucket.bytes}
    $manifestPath=Join-Path $directory 'partition.json';Write-Json $manifestPath $manifest
    $manifests.Add([pscustomobject]@{partitionId=$id;manifestPath=$manifestPath;sourceListPath=$listPath;sourceFileCount=$sourceFiles.Count;nonsharedSourceFileCount=$bucket.rows.Count;sourceBytes=$bucket.bytes;testDeclarationCount=$manifest.testDeclarations.Count})
}
$plan=[ordered]@{schemaVersion=1;projectPath=$project;originalCompileMetadata=$inputIdentity;syntaxParser=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree].Assembly.FullName;originalSourceFiles=@($rows|ForEach-Object path|Sort-Object);originalSourceBytes=$totalBytes;sourceDigests=@($rows|Sort-Object path|Select-Object path,bytes,sha256);assignmentPreservation=$preservedAssignment;sharedSourceMode=$SharedSourceMode;alwaysSharedSourceFiles=@($alwaysShared|ForEach-Object path);sharedSourceFiles=@($shared|ForEach-Object path);sharedSourceBytes=$sharedBytes;componentCount=$components.Count;largestComponents=@($components|Sort-Object bytes -Descending|Select-Object -First 6 @{Name='sourceFileCount';Expression={$_.rows.Count}},bytes,key);partitionCount=$PartitionCount;maximumPartitionSourceBytes=$MaximumPartitionSourceBytes;partitions=$manifests;coverage=@{originalCompileCount=$paths.Count;uniqueUnionCount=$coverage.Count;nonsharedMutuallyExclusive=$true;testDeclarationsMutuallyExclusive=$true;testDeclarationCount=$testCoverage.Count;requiresActualBuildAndDiscovery=$true}}
Write-Json (Join-Path $output 'partition-plan.json') $plan
[pscustomobject]@{planPath=(Join-Path $output 'partition-plan.json');originalCompileCount=$paths.Count;sharedSourceCount=$shared.Count;partitionCount=$PartitionCount;testDeclarationCount=$testCoverage.Count;coverageVerified=$true;requiresActualBuildAndDiscovery=$true}|ConvertTo-Json -Compress

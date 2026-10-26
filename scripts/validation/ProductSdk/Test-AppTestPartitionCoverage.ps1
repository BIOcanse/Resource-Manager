#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanPath,
    [string[]]$TestsRunDirectories = @(),
    [string[]]$ExecutionSummaryPaths = @(),
    [string]$ReviewedToolChangesPath,
    [switch]$FunctionsOnly,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$MetadataLoadContextPath = 'C:\Program Files\dotnet\sdk\10.0.300\System.Reflection.MetadataLoadContext.dll'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$issues = [Collections.Generic.List[string]]::new()
$partitions = [Collections.Generic.List[object]]::new()
$result = [ordered]@{schemaVersion=1; accepted=$false; fullCoverage=$false; metadataOnly=$true; issues=@()}
function Check([bool]$Condition,[string]$Message) { if(-not $Condition){throw $Message} }
function Read-Json([string]$Path) {
    Check ([IO.File]::Exists($Path)) "Missing evidence: $Path"
    Check (([IO.FileInfo]$Path).Length -le 67108864) "JSON exceeds 64 MiB: $Path"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Verify-Hash([string]$Path,[string]$Expected) { Check ((Hash $Path) -ieq $Expected) "Evidence hash differs: $Path" }
function New-Set($Values,[switch]$Ordinal) {
    $comparer=if($Ordinal){[StringComparer]::Ordinal}else{[StringComparer]::OrdinalIgnoreCase}
    $set=[Collections.Generic.HashSet[string]]::new($comparer)
    foreach($value in @($Values)){if($null -eq $value){continue};Check ($set.Add([string]$value)) "Duplicate set entry: $value"}
    return ,$set
}
function Lines([string]$Path) {
    Check ([IO.File]::Exists($Path)) "Missing Compile evidence: $Path"
    return @(Get-Content -LiteralPath $Path | Where-Object {-not [string]::IsNullOrWhiteSpace($_)} | ForEach-Object {[IO.Path]::GetFullPath($_)})
}
function Receipt([string]$Run,[string]$Stage) {
    $receipt=Read-Json (Join-Path $Run 'bounded-result.json')
    Check ($receipt.stage -eq $Stage -and $receipt.accepted -and $receipt.normalDrain -and $receipt.sourceInputsUnchangedExceptDeclaredPartitionRefresh -and $receipt.exitCode -eq 0) "Unaccepted, unstable or undrained $Stage run: $Run"
    return $receipt
}
function Normalize-Class([string]$Name) { return $Name.Replace('+','.') }
function Source-MethodKey([string]$Declaration) {
    $split=$Declaration.Split(@('::'),2,[StringSplitOptions]::None)
    Check ($split.Length -eq 2) "Malformed planned test declaration: $Declaration"
    $method=[Microsoft.CodeAnalysis.CSharp.SyntaxFactory]::ParseMemberDeclaration('void '+$split[1]+' {}')
    Check ($null -ne $method -and $method -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and -not $method.ContainsDiagnostics) "Cannot parse planned method signature: $Declaration"
    $arity=if($null -eq $method.TypeParameterList){0}else{$method.TypeParameterList.Parameters.Count}
    return $split[0]+'::'+$method.Identifier.ValueText+'`'+$arity+'/'+$method.ParameterList.Parameters.Count
}
function Root-TypeKey($Type) {
    $root=$Type;$depth=0
    while($null -ne $root.DeclaringType){Check ((++$depth) -le 64) 'Nested type depth exceeds bound.';$root=$root.DeclaringType}
    return $root.Namespace+'|'+$root.Name
}
function Has-TestAttribute($Method) {
    $methods=@($Method)
    # MetadataLoadContext deliberately does not implement GetBaseDefinition.
    # Follow override signatures using metadata, without loading executable code.
    if($Method.IsVirtual -and ($Method.Attributes -band [Reflection.MethodAttributes]::NewSlot) -eq 0) {
        $baseType=$Method.DeclaringType.BaseType;$baseDepth=0
        $signature=[string]::Join('|',@($Method.GetParameters()|ForEach-Object {$_.ParameterType.ToString()}))
        while($null -ne $baseType) {
            Check ((++$baseDepth) -le 64) 'Method inheritance exceeds bound.'
            $matches=@($baseType.GetMethods([Reflection.BindingFlags]'DeclaredOnly,Public,NonPublic,Instance')|Where-Object {
                $_.Name -ceq $Method.Name -and $_.GetGenericArguments().Count -eq $Method.GetGenericArguments().Count -and
                [string]::Join('|',@($_.GetParameters()|ForEach-Object {$_.ParameterType.ToString()})) -ceq $signature
            })
            Check ($matches.Count -le 1) 'Ambiguous inherited method signature.'
            if($matches.Count){$methods+=,$matches[0];if(($matches[0].Attributes -band [Reflection.MethodAttributes]::NewSlot) -ne 0){break}}
            $baseType=$baseType.BaseType
        }
    }
    foreach($candidate in $methods) {
        foreach($attribute in $candidate.GetCustomAttributesData()) {
            $type=$attribute.AttributeType;$depth=0
            while($null -ne $type) {
                Check ((++$depth) -le 64) 'Attribute inheritance exceeds bound.'
                if($type.FullName -in @('Xunit.FactAttribute','Xunit.TheoryAttribute')){return $true}
                $type=$type.BaseType
            }
        }
    }
    return $false
}
function Inspect-Metadata([string]$AssemblyPath,[string]$OriginalBin,$References) {
    # Only the SDK's metadata reader is loaded for execution. Test assemblies are
    # loaded exclusively into MetadataLoadContext; no constructors/initializers run.
    if(-not ('System.Reflection.MetadataLoadContext' -as [type])){Add-Type -Path $MetadataLoadContextPath}
    $paths=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $candidates=[Collections.Generic.List[string]]::new();$candidates.Add($AssemblyPath)
    if([IO.Directory]::Exists($OriginalBin)){foreach($file in [IO.Directory]::EnumerateFiles($OriginalBin,'*.dll')){$candidates.Add($file)}}
    foreach($file in @($References)){$candidates.Add($file)}
    foreach($file in ([string][AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES')).Split([IO.Path]::PathSeparator)){$candidates.Add($file)}
    Check ($candidates.Count -le 8192) 'Metadata resolver exceeds assembly bound.'
    foreach($file in $candidates) {
        try {$name=[Reflection.AssemblyName]::GetAssemblyName($file).Name}
        catch [BadImageFormatException] {continue}
        if(-not $paths.ContainsKey($name)){$paths.Add($name,$file)}
    }
    $resolver=[Reflection.PathAssemblyResolver]::new([string[]]@($paths.Values))
    $context=[Reflection.MetadataLoadContext]::new($resolver,'System.Private.CoreLib')
    try {
        $assembly=$context.LoadFromAssemblyPath($AssemblyPath)
        $types=@($assembly.GetTypes());Check ($types.Count -le 50000) 'Metadata type bound exceeded.'
        $declared=[Collections.Generic.List[object]]::new();$discoverable=[Collections.Generic.List[object]]::new()
        $openTypes=[Collections.Generic.List[string]]::new();$methodCount=0
        foreach($type in $types) {
            foreach($method in $type.GetMethods([Reflection.BindingFlags]'DeclaredOnly,Public,NonPublic,Instance,Static')) {
                Check ((++$methodCount) -le 250000) 'Metadata method bound exceeded.'
                if(Has-TestAttribute $method) {
                    $arity=if($method.IsGenericMethod){$method.GetGenericArguments().Count}else{0}
                    $declared.Add([pscustomobject]@{root=(Root-TypeKey $type);declaringType=$type.FullName;method=$method.Name
                        sourceKey=(Root-TypeKey $type)+'::'+$method.Name+'`'+$arity+'/'+$method.GetParameters().Count
                        signature=$method.ToString();abstract=$type.IsAbstract;nested=$type.IsNested})
                }
            }
            if(-not $type.IsClass -or $type.IsAbstract){continue}
            # xUnit v2 discovers attributed nonpublic methods as well. Keep the
            # metadata union aligned with actual runner discovery, including
            # internal Theory methods, without executing the test assembly.
            $testMethods=@($type.GetMethods([Reflection.BindingFlags]'Public,NonPublic,Instance,Static')|Where-Object {Has-TestAttribute $_})
            if($type.ContainsGenericParameters){if($testMethods.Count){$openTypes.Add($type.FullName)};continue}
            foreach($method in $testMethods) {
                    $discoverable.Add([pscustomobject]@{key=(Normalize-Class $type.FullName)+'.'+$method.Name
                        className=$type.FullName;method=$method.Name;declaringType=$method.DeclaringType.FullName
                        inherited=$method.DeclaringType.FullName -ne $type.FullName;nested=$type.IsNested
                        sourceRoot=(Root-TypeKey $method.DeclaringType);signature=$method.ToString()})
            }
        }
        return [pscustomobject]@{assemblyName=$assembly.GetName().Name;declared=@($declared.ToArray());discoverable=@($discoverable.ToArray());openGenericClasses=@($openTypes.ToArray())}
    } finally {$context.Dispose()}
}
if($FunctionsOnly){return}
try {
    Check ($TestsRunDirectories.Count -gt 0 -or $ExecutionSummaryPaths.Count -gt 0) 'Provide TestsRunDirectories or ExecutionSummaryPaths.'
    $planFile=[IO.Path]::GetFullPath($PlanPath);$plan=Read-Json $planFile;$planHash=Hash $planFile
    Check ($plan.schemaVersion -eq 1) 'Unsupported partition plan.'
    $original=New-Set $plan.originalSourceFiles;$shared=New-Set $plan.sharedSourceFiles
    Check ($original.Count -le 4096 -and @($plan.partitions).Count -le 32) 'Plan exceeds source/partition bounds.'
    Check ($shared.IsSubsetOf($original)) 'Shared sources lie outside original Compile.'
    $dependencyScoped=$plan.PSObject.Properties.Name -contains 'sharedSourceMode' -and $plan.sharedSourceMode -ceq 'DependencyClosure'
    $alwaysShared=if($dependencyScoped){New-Set $plan.alwaysSharedSourceFiles}else{$shared}
    Check ($alwaysShared.IsSubsetOf($shared)) 'Global initialization lies outside shared sources.'
    Verify-Hash $plan.originalCompileMetadata.path $plan.originalCompileMetadata.sha256
    $baseline=Read-Json $plan.originalCompileMetadata.path
    Check ($original.SetEquals((New-Set @($baseline.Items.Compile|ForEach-Object FullPath)))) 'Plan original sources differ from original evaluated Compile.'
    $repo=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($plan.projectPath)) '../..'))
    $baseProductionRoots=@('src/Core','src/FreedomPointContracts','src/ReadWriteCore','src/SubscriptionCore','src/Shared','sdk/csharp/ResourceManager.Adapter.Abstractions','sdk/csharp/ResourceManager.Adapter.SharedMemory')
    $fullProductionRoots=@('src/UI/Core','src/UI/Launcher','src/SessionAgent','src/Installer','src/Updater','src/Shared','src/Core','src/FreedomPointContracts','src/ReadWriteCore','src/SubscriptionCore','src/UI/Frontend/src','src/UI/Frontend/tests')
    $productionPrefixes=@(@($baseProductionRoots)+@('src/UI/Core','src/UI/Launcher','src/SessionAgent','src/Installer','src/Updater','src/UI/Frontend')|ForEach-Object {[IO.Path]::GetFullPath((Join-Path $repo $_))+[IO.Path]::DirectorySeparatorChar})
    $productionFiles=@(& rg --files @($baseProductionRoots|ForEach-Object {Join-Path $repo $_}));Check ($LASTEXITCODE -eq 0) 'Cannot enumerate current production inputs.'
    $productionFiles=@($productionFiles|Where-Object {$_ -notmatch '(^|[\\/])(bin|obj|node_modules)([\\/]|$)' -and $_ -match '\.(cs|csproj|props|targets|json|ps1|mjs|rsp|runsettings)$'})
    $fullProductionFiles=@(& rg --files @($fullProductionRoots|ForEach-Object {Join-Path $repo $_}));Check ($LASTEXITCODE -eq 0) 'Cannot enumerate full App production inputs.'
    $productionFiles+=@($fullProductionFiles|Where-Object {$_ -notmatch '(^|[\\/])(bin|obj|node_modules|zig-out|\.zig-cache)([\\/]|$)' -and $_ -notmatch '[\\/]src[\\/]Core[\\/]Native[\\/]'})
    $productionFiles+=@('src/UI/Frontend/package.json','src/UI/Frontend/package-lock.json')|ForEach-Object {Join-Path $repo $_}|Where-Object {[IO.File]::Exists($_)}
    $currentProduction=New-Set @($productionFiles|ForEach-Object {[IO.Path]::GetFullPath($_)}|Sort-Object -Unique)
    $toolFiles=@(& rg --files (Join-Path $repo 'scripts/validation') (Join-Path $repo 'scripts/build'));Check ($LASTEXITCODE -eq 0) 'Cannot enumerate validation tools.'
    $currentTools=New-Set @($toolFiles|Where-Object {$_ -notmatch '(^|[\\/])(bin|obj|node_modules)([\\/]|$)' -and $_ -match '\.(cs|csproj|props|targets|json|ps1|mjs|rsp|runsettings)$'}|ForEach-Object {[IO.Path]::GetFullPath($_)})
    Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp
    $runCandidates=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($run in $TestsRunDirectories){[void]$runCandidates.Add([IO.Path]::GetFullPath($run))}
    foreach($summaryPath in $ExecutionSummaryPaths) {
        $summary=Read-Json $summaryPath
        Check ($summary.planSha256 -ieq $planHash) "Execution summary belongs to another plan: $summaryPath"
        foreach($row in @($summary.results)) {
            $receipts=if($row.PSObject.Properties.Name -contains 'runtimeTests' -and @($row.runtimeTests).Count -gt 0){@($row.runtimeTests)}else{@($row.tests)}
            foreach($receipt in $receipts){if($null -ne $receipt -and $receipt.accepted){[void]$runCandidates.Add([IO.Path]::GetFullPath($receipt.runDirectory))}}
        }
    }
    Check ($runCandidates.Count -le 512) 'Too many candidate Tests receipts.'
    $chosen=@{};$superseded=[Collections.Generic.List[string]]::new()
    foreach($run in $runCandidates) {
        $testReceipt=Receipt $run 'Tests'
        $build=Read-Json (Join-Path $run 'partition-tests-build.json');$id=[string]$build.partitionId
        Check (@($plan.partitions|Where-Object partitionId -CEQ $id).Count -eq 1) "Unknown partition in Tests run: $id"
        $stamp=([IO.FileInfo](Join-Path $run 'bounded-result.json')).LastWriteTimeUtc
        $selectionPath=Join-Path $run 'test-runtime-selection.json'
        $selection=if([IO.File]::Exists($selectionPath)){Read-Json $selectionPath}else{$null}
        $groupKey=if($null -eq $selection){'unfiltered'}else{[string]$selection.groupId}
        Check (-not [string]::IsNullOrWhiteSpace($groupKey)) "Empty runtime group: $run"
        if(-not $chosen.ContainsKey($id)){$chosen[$id]=@{}}
        if(-not $chosen[$id].ContainsKey($groupKey) -or $stamp -gt $chosen[$id][$groupKey].stamp) {
            if($chosen[$id].ContainsKey($groupKey)){$superseded.Add($chosen[$id][$groupKey].run)}
            $chosen[$id][$groupKey]=@{run=$run;build=$build;stamp=$stamp;selection=$selection;receipt=$testReceipt}
        } else {$superseded.Add($run)}
    }
    $actualUnion=New-Set @();$nonsharedOwner=@{};$methodOwner=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal);$declarationUnion=New-Set @() -Ordinal
    $totals=@{total=0L;passed=0L;failed=0L;notExecuted=0L}
    $finalBaseline=@{};$missingPartitions=[Collections.Generic.List[string]]::new()
    $toolChanges=[Collections.Generic.List[object]]::new()
    $unreviewedTools=[Collections.Generic.List[string]]::new()
    $reviews=if($ReviewedToolChangesPath){@( (Read-Json $ReviewedToolChangesPath).changes )}else{@()}
    foreach($entry in @($plan.partitions)) {
        $id=[string]$entry.partitionId
        if(-not $chosen.ContainsKey($id)){$missingPartitions.Add($id);continue}
        $runs=@($chosen[$id].Values|Sort-Object {$_.run})
        Check ($runs.Count -le 512) "Too many runtime groups: $id"
        Check (-not ($chosen[$id].ContainsKey('unfiltered') -and $runs.Count -ne 1)) "Cannot mix unfiltered and grouped Tests: $id"
        $run=$runs[0].run;$context=$runs[0].build;$buildRun=[string]$context.runDirectory
        [void](Receipt $buildRun 'Build');$built=Read-Json (Join-Path $buildRun 'partition-build.json')
        $manifest=Read-Json $entry.manifestPath;$manifestHash=Hash $entry.manifestPath
        Check ($context.buildSucceeded -and $built.buildSucceeded -and $built.cscInvoked -and $context.partitionId -ceq $id -and $built.partitionId -ceq $id) "Build partition mismatch: $id"
        foreach($proof in @($context,$built)) {
            Check ($proof.testCompileManifestSha256 -ieq $manifestHash -and $proof.testProject -ieq $plan.projectPath) "Build is not bound to this manifest/project: $id"
            Check ($proof.originalCompileMetadata.sha256 -ieq $plan.originalCompileMetadata.sha256) "Original Compile identity changed: $id"
            Verify-Hash $proof.sourceListPath $proof.sourceListSha256
        }
        Verify-Hash (Join-Path $run 'test-compile-manifest.json') $manifestHash
        Verify-Hash (Join-Path $buildRun 'test-compile-manifest.json') $manifestHash
        Verify-Hash (Join-Path $run 'requested-sources.txt') $context.sourceListSha256
        foreach($proof in @($built.compileEvidence)){Verify-Hash $proof.path $proof.sha256}
        $evidence=Join-Path $buildRun 'compile-partition'
        $sealedEvidence=New-Set @($built.compileEvidence|ForEach-Object path)
        foreach($name in @('original-compile','selected-compile','generated-compile','analyzers','references','embedded-resources','project-references','additional-files','properties')) {
            Check ($sealedEvidence.Contains((Join-Path $evidence ($name+'.txt')))) "Compiler evidence is not hash-bound: $id/$name"
        }
        Verify-Hash (Join-Path $evidence 'selected-compile.txt') $built.selectedCompileSha256
        $selected=New-Set (Lines (Join-Path $evidence 'selected-compile.txt'))
        $generated=New-Set (Lines (Join-Path $evidence 'generated-compile.txt'))
        $originalActual=New-Set (Lines (Join-Path $evidence 'original-compile.txt'))
        $expected=New-Set $manifest.sourceFiles
        Check ($manifest.partitionId -ceq $id -and $manifest.sourceFiles.Count -eq $entry.sourceFileCount) "Planned partition identity/count differs: $id"
        Check ($expected.IsSubsetOf($original) -and $alwaysShared.IsSubsetOf($expected)) "Partition sources do not preserve original/shared scope: $id"
        $localShared=New-Set $manifest.sharedSourceFiles
        if($dependencyScoped){
            Check ($localShared.IsSubsetOf($shared) -and $alwaysShared.IsSubsetOf($localShared)) "Dependency-scoped shared sources differ: $id"
            Check ($localShared.SetEquals((New-Set @($manifest.sourceFiles|Where-Object {$shared.Contains($_)})))) "Shared manifest subset differs from actual planned sources: $id"
        }else{Check ($localShared.SetEquals($shared)) "Shared source list differs: $id"}
        Check ($original.IsSubsetOf($originalActual)) "Original Compile omitted planned source: $id"
        $sourceHashes=@{}
        foreach($row in @(Read-Json (Join-Path $buildRun 'source-inputs.json'))) {
            $path=if([IO.Path]::IsPathRooted($row.path)){[IO.Path]::GetFullPath($row.path)}else{[IO.Path]::GetFullPath((Join-Path $repo $row.path))}
            $sourceHashes[$path]=[string]$row.sha256
        }
        $recordedProduction=New-Set @($sourceHashes.Keys|Where-Object {$path=$_;@($productionPrefixes|Where-Object {$path.StartsWith($_,[StringComparison]::OrdinalIgnoreCase)}).Count -gt 0})
        Check ($currentProduction.IsSubsetOf($recordedProduction)) "Current production input is absent from accepted Build: $id"
        foreach($source in $recordedProduction){Verify-Hash $source $sourceHashes[$source]}
        foreach($rootInput in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json','NuGet.Config')) {
            $rootPath=Join-Path $repo $rootInput
            if($sourceHashes.ContainsKey($rootPath)){Verify-Hash $rootPath $sourceHashes[$rootPath]}
        }
        $recordedTools=New-Set @($sourceHashes.Keys|Where-Object { $_.StartsWith((Join-Path $repo 'scripts/validation')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $_.StartsWith((Join-Path $repo 'scripts/build')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) })
        $allTools=New-Set @(@($currentTools)+@($recordedTools)|Sort-Object -Unique)
        foreach($tool in $allTools) {
            $previous=if($sourceHashes.ContainsKey($tool)){$sourceHashes[$tool]}else{$null}
            $current=if([IO.File]::Exists($tool)){Hash $tool}else{$null}
            if($previous -ine $current) {
                $change=[pscustomobject]@{partitionId=$id;path=$tool;previousSha256=$previous;currentSha256=$current}
                $toolChanges.Add($change)
                if(@($reviews|Where-Object {$_.path -ieq $tool -and $_.previousSha256 -ieq $previous -and $_.currentSha256 -ieq $current}).Count -ne 1){$unreviewedTools.Add("$tool ($id)")}
            }
        }
        foreach($source in $selected) {
            if($original.Contains($source)) {
                Check ($expected.Contains($source)) "Unexpected original Compile selected in $id : $source"
                Check ($sourceHashes.ContainsKey($source)) "Selected source lacks input hash: $source"
                Verify-Hash $source $sourceHashes[$source]
                [void]$actualUnion.Add($source)
                if(-not $shared.Contains($source)){Check (-not $nonsharedOwner.ContainsKey($source)) "Nonshared source occurs twice: $source";$nonsharedOwner[$source]=$id}
            } else {Check ($generated.Contains($source)) "Unexpected non-SDK-generated Compile item: $source"}
        }
        foreach($source in $expected){Check ($selected.Contains($source)) "Planned source was not compiled: $source"}
        foreach($source in $originalActual){Check ($original.Contains($source) -or $generated.Contains($source)) "Original Compile has unexplained extra item: $source"}
        $properties=@{};foreach($line in Get-Content (Join-Path $evidence 'properties.txt')){$p=$line.Split('=',2);$properties[$p[0]]=$p[1]}
        Check ($properties.BuildProjectReferences -ieq 'true') "Accepted App compile disabled production project-reference builds: $id"
        Check ($properties.AssemblyName -ceq $baseline.Properties.AssemblyName -and $properties.MSBuildProjectFullPath -ieq $plan.projectPath) "Original assembly/project identity was changed: $id"
        foreach($property in @('RunAnalyzers','RunAnalyzersDuringBuild')){Check ($properties[$property] -ine 'false') "Analyzer execution disabled: $id/$property"}
        $surface=@{}
        foreach($kind in @('analyzers','references','embedded-resources','project-references','additional-files')) {
            $surface[$kind]=New-Set (Lines (Join-Path $evidence ($kind+'.txt')))
            if($finalBaseline.ContainsKey($kind)){Check ($surface[$kind].SetEquals($finalBaseline[$kind])) "Final compiler $kind differ between partitions: $id"}
            else {$finalBaseline[$kind]=$surface[$kind]}
        }
        Check ($surface['analyzers'].Count -gt 0 -and $surface['references'].Count -gt 0) "Compiler analyzers/references are empty: $id"
        Check ((New-Set @($baseline.Items.Analyzer|ForEach-Object FullPath)).IsSubsetOf($surface['analyzers'])) "Original analyzers were removed: $id"
        Check ((New-Set @($baseline.Items.EmbeddedResource|ForEach-Object FullPath)).SetEquals($surface['embedded-resources'])) "Original embedded resources changed: $id"
        Check ((New-Set @($baseline.Items.ProjectReference|ForEach-Object FullPath)).IsSubsetOf($surface['project-references'])) "Original project references were removed: $id"
        $buildOutputs=@(Read-Json (Join-Path $buildRun 'test-output-build.json'))
        foreach($row in $buildOutputs|Where-Object exists){Verify-Hash $row.archive $row.sha256}
        $dll=@($buildOutputs|Where-Object {$_.exists -and [IO.Path]::GetExtension($_.path) -ieq '.dll'})
        Check ($dll.Count -eq 1 -and $dll[0].sha256 -ieq $context.testAssemblySha256 -and $built.testAssemblySha256 -ieq $context.testAssemblySha256) "Tests are not bound to the archived Build DLL: $id"
        $metadata=Inspect-Metadata $dll[0].archive ([IO.Path]::GetDirectoryName($context.testAssemblyPath)) @($surface['references'])
        Check ($metadata.assemblyName -ceq $baseline.Properties.AssemblyName) "Archived metadata assembly name changed: $id"
        $plannedMethods=@{};foreach($declaration in @($manifest.testDeclarations)) {
            Check ($declarationUnion.Add($declaration)) "Test declaration planned in multiple partitions: $declaration"
            $key=Source-MethodKey $declaration;if(-not $plannedMethods.ContainsKey($key)){$plannedMethods[$key]=0};$plannedMethods[$key]++
        }
        $compiledMethods=@{};foreach($method in $metadata.declared){$key=$method.sourceKey;if(-not $compiledMethods.ContainsKey($key)){$compiledMethods[$key]=0};$compiledMethods[$key]++}
        $methodCounts=@(foreach($key in $plannedMethods.Keys){[pscustomobject]@{key=$key;count=$plannedMethods[$key]}})
        $compiledCounts=@(foreach($key in $compiledMethods.Keys){[pscustomobject]@{key=$key;count=$compiledMethods[$key]}})
        $declarationDiff=@(Compare-Object $methodCounts $compiledCounts -Property key,count)
        Check ($declarationDiff.Count -eq 0) ("Source Fact/Theory declarations and compiled metadata differ: $id; "+(($declarationDiff|Select-Object -First 12|ForEach-Object {"$($_.SideIndicator)$($_.key):$($_.count)"}) -join '; '))
        $expectedMethods=New-Set @($metadata.discoverable|ForEach-Object key|Sort-Object -Unique -CaseSensitive) -Ordinal
        Check ($metadata.openGenericClasses.Count -eq 0) "Open generic nonabstract test classes need explicit discovery reconciliation: $id"
        $runtimeManifest=$null;$runtimeHash=$null;$runtimeMethods=@{}
        if(-not $chosen[$id].ContainsKey('unfiltered')) {
            $runtimeManifestPath=Join-Path $run 'test-runtime-manifest.json'
            $runtimeManifest=Read-Json $runtimeManifestPath;$runtimeHash=Hash $runtimeManifestPath
            Check ($runtimeManifest.schemaVersion -eq 1 -and $runtimeManifest.kind -ceq 'AppTestRuntimePartitions' -and $runtimeManifest.partitionId -ceq $id) "Invalid runtime manifest: $id"
            Check ($runtimeManifest.planSha256 -ieq $planHash -and $runtimeManifest.testCompileManifestSha256 -ieq $manifestHash -and $runtimeManifest.testCompileManifest -ieq $entry.manifestPath) "Runtime manifest compile/plan mismatch: $id"
            Check ($runtimeManifest.testAssemblySha256 -ieq $built.testAssemblySha256 -and $runtimeManifest.buildRunDirectory -ieq $buildRun) "Runtime groups do not use the same accepted Build/DLL: $id"
            Verify-Hash (Join-Path $buildRun 'partition-build.json') $runtimeManifest.partitionBuildSha256
            Verify-Hash $runtimeManifest.assemblyArchive $built.testAssemblySha256
            foreach($tool in $runtimeManifest.tools) {
                $runtimeToolPath=[IO.Path]::GetFullPath($tool.path)
                $runtimeToolHash=Hash $runtimeToolPath
                if($runtimeToolHash -ine $tool.sha256) {
                    Check ($currentTools.Contains($runtimeToolPath) -and @($reviews|Where-Object {$_.path -ieq $runtimeToolPath -and $_.previousSha256 -ieq $tool.sha256 -and $_.currentSha256 -ieq $runtimeToolHash}).Count -eq 1) "Runtime metadata tool change lacks exact review: $runtimeToolPath"
                }
            }
            $allRuntimeMethods=New-Set @() -Ordinal
            foreach($group in $runtimeManifest.groups) {
                Check (-not $runtimeMethods.ContainsKey($group.groupId) -and $group.groupId -match '^run-\d{3,}$') "Duplicate/invalid runtime group: $id"
                $methods=New-Set @($group.methods) -Ordinal
                Check ($methods.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($group.filter)) "Empty runtime group: $id"
                foreach($method in $methods){Check ($allRuntimeMethods.Add($method)) "Runtime groups overlap: $method"}
                $runtimeMethods[$group.groupId]=$methods
            }
            Check ($allRuntimeMethods.SetEquals($expectedMethods) -and (New-Set @($runtimeManifest.methods) -Ordinal).SetEquals($expectedMethods)) "Runtime manifest method union differs from metadata: $id"
            Check ((New-Set @($chosen[$id].Keys) -Ordinal).SetEquals((New-Set @($runtimeMethods.Keys) -Ordinal))) "Missing or unexpected runtime groups: $id"
        }
        $runReports=[Collections.Generic.List[object]]::new()
        foreach($testRun in $runs) {
        $run=$testRun.run;$testContext=$testRun.build
        Check ($testContext.runDirectory -ieq $buildRun -and $testContext.testAssemblySha256 -ieq $built.testAssemblySha256 -and $testContext.testCompileManifestSha256 -ieq $manifestHash) "Tests runtime groups mix Build/DLL/compile manifest: $id"
        Verify-Hash (Join-Path $run 'test-compile-manifest.json') $manifestHash
        Verify-Hash (Join-Path $run 'requested-sources.txt') $built.sourceListSha256
        $groupId=$null;$expectedRunMethods=$expectedMethods
        if($null -ne $runtimeManifest) {
            $selection=$testRun.selection;$groupId=[string]$selection.groupId
            Verify-Hash (Join-Path $run 'test-runtime-manifest.json') $runtimeHash
            Verify-Hash $selection.runtimeManifestPath $runtimeHash
            $group=@($runtimeManifest.groups|Where-Object groupId -CEQ $groupId)[0]
            Check ($selection.schemaVersion -eq 1 -and $selection.partitionId -ceq $id -and $selection.runtimeManifestSha256 -ieq $runtimeHash -and $selection.testAssemblySha256 -ieq $built.testAssemblySha256 -and $selection.filter -ceq $group.filter) "Runtime Tests selection differs from exact manifest group: $id/$groupId"
            Check ($testRun.receipt.testRuntimeManifestSha256 -ieq $runtimeHash -and $testRun.receipt.testRuntimeGroupId -ceq $groupId -and $testRun.receipt.testRuntimeManifest -ieq $selection.runtimeManifestPath) "Bounded Tests receipt does not bind the runtime selection: $id/$groupId"
            $sealedRuntime=@{}
            foreach($row in @(Read-Json (Join-Path $run 'source-inputs.json'))) {
                $sealedPath=if([IO.Path]::IsPathRooted($row.path)){[IO.Path]::GetFullPath($row.path)}else{[IO.Path]::GetFullPath((Join-Path $repo $row.path))}
                $sealedRuntime[$sealedPath]=$row.sha256
            }
            foreach($runtimeInput in @([pscustomobject]@{path=$selection.runtimeManifestPath;sha256=$runtimeHash})+@($runtimeManifest.tools)) {
                Check ($sealedRuntime.ContainsKey($runtimeInput.path) -and $sealedRuntime[$runtimeInput.path] -ieq $runtimeInput.sha256) "Runtime manifest/tool is not sealed into Tests inputs: $($runtimeInput.path)"
            }
            $expectedRunMethods=$runtimeMethods[$groupId]
        }
        $archives=@{}
        foreach($label in @('before-tests','after-tests')) {
            $rows=@(Read-Json (Join-Path $run ('test-output-'+$label+'.json')))
            foreach($row in $rows|Where-Object exists){Verify-Hash $row.archive $row.sha256}
            $archives[$label]=$rows
            Check (@(Compare-Object $buildOutputs $rows -Property path,exists,sha256).Count -eq 0) "Built App DLL/PDB/deps/runtimeconfig changed around Tests: $id/$label"
        }
        $trxPath=Join-Path $run 'full-app.trx';Check (([IO.FileInfo]$trxPath).Length -le 134217728) "TRX exceeds 128 MiB: $id"
        $xml=[Xml.XmlDocument]::new();$xml.XmlResolver=$null;$xml.Load($trxPath)
        $definitions=@{};$observed=New-Set @() -Ordinal
        foreach($definition in $xml.SelectNodes("/*[local-name()='TestRun']/*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
            $method=$definition.SelectSingleNode("*[local-name()='TestMethod']")
            Check ($null -ne $method) "TRX TestDefinition has no TestMethod: $id"
            $key=(Normalize-Class $method.GetAttribute('className'))+'.'+$method.GetAttribute('name')
            Check (-not $definitions.ContainsKey($definition.GetAttribute('id'))) "Duplicate TRX definition id: $id"
            $definitions[$definition.GetAttribute('id')]=$key
        }
        $outcomes=@{};$resultCount=0L;$usedIds=New-Set @() -Ordinal
        foreach($reading in $xml.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
            $testId=$reading.GetAttribute('testId');Check ($definitions.ContainsKey($testId)) "TRX result has no matching TestDefinition: $id/$testId"
            [void]$usedIds.Add($testId);[void]$observed.Add($definitions[$testId]);$resultCount++
            $outcome=$reading.GetAttribute('outcome');if(-not $outcomes.ContainsKey($outcome)){$outcomes[$outcome]=0L};$outcomes[$outcome]++
        }
        Check ($usedIds.Count -eq $definitions.Count) "TRX definitions without actual results: $id"
        $missingMethods=@($expectedRunMethods|Where-Object {-not $observed.Contains($_)})
        $unexpectedMethods=@($observed|Where-Object {-not $expectedRunMethods.Contains($_)})
        Check ($observed.SetEquals($expectedRunMethods)) ("TRX TestMethod union differs from exact metadata runtime group: $id/$groupId; missing="+(($missingMethods|Select-Object -First 12) -join ',')+'; unexpected='+(($unexpectedMethods|Select-Object -First 12) -join ','))
        foreach($key in $observed){Check (-not $methodOwner.ContainsKey($key)) "Actual test method appears in multiple partitions: $key";$methodOwner[$key]=$id}
        $counter=$xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
        Check ($null -ne $counter -and [long]$counter.GetAttribute('total') -eq $resultCount) "TRX total differs from actual results: $id"
        $rawCounters=[ordered]@{};foreach($attribute in $counter.Attributes){$rawCounters[$attribute.Name]=[long]$attribute.Value}
        foreach($mapping in @(@('passed','Passed'),@('failed','Failed'),@('notExecuted','NotExecuted'))) {
            $actual=if($outcomes.ContainsKey($mapping[1])){$outcomes[$mapping[1]]}else{0L}
            # xunit/VSTest can leave Counters.notExecuted at zero for skipped
            # UnitTestResult nodes. Preserve it, but derive skips from Results.
            if($mapping[0] -ne 'notExecuted'){Check ([long]$counter.GetAttribute($mapping[0]) -eq $actual) "TRX outcome counter differs: $id/$($mapping[0])"}
            $totals[$mapping[0]]+=$actual
        }
        $totals.total+=$resultCount
        Check ([long]$counter.GetAttribute('failed') -eq 0) "Accepted Tests run nevertheless reports failures: $id"
        Check (@($outcomes.Keys|Where-Object {$_ -notin @('Passed','Failed','NotExecuted')}).Count -eq 0) "Unreconciled TRX result outcome: $id"
        $runReports.Add([pscustomobject]@{groupId=$groupId;testsRun=$run;testCases=$resultCount;actualTestMethods=@($observed|Sort-Object);trxPath=$trxPath;trxSha256=(Hash $trxPath);outcomes=$outcomes;rawCounters=$rawCounters
            skippedFromResults=$(if($outcomes.ContainsKey('NotExecuted')){$outcomes['NotExecuted']}else{0L})})
        }
        $partitions.Add([pscustomobject]@{partitionId=$id;runtimeManifestSha256=$runtimeHash;runs=@($runReports.ToArray());buildRun=$buildRun;manifestSha256=$manifestHash
            originalSources=$expected.Count;sdkGeneratedSources=@($selected|Where-Object {-not $original.Contains($_)})
            analyzerCount=$surface['analyzers'].Count;referenceCount=$surface['references'].Count;embeddedResourceCount=$surface['embedded-resources'].Count
            projectReferenceCount=$surface['project-references'].Count;assemblyArchive=$dll[0].archive;assemblySha256=$dll[0].sha256
            sourceDeclarations=$manifest.testDeclarations.Count;metadataDeclarations=$metadata.declared.Count;discoveredMethods=$expectedMethods.Count
            inheritedMethods=@($metadata.discoverable|Where-Object inherited);nestedMethods=@($metadata.discoverable|Where-Object nested)
            declarationRootMapping=$metadata.declared;actualTestMethods=@($expectedMethods|Sort-Object)})
    }
    $complete=$missingPartitions.Count -eq 0
    if($complete) {
        Check ($actualUnion.SetEquals($original)) 'Actual selected Compile union does not equal all original source files.'
        Check ($nonsharedOwner.Count -eq $original.Count-$shared.Count) 'Nonshared Compile coverage differs.'
        Check ($declarationUnion.Count -eq $plan.coverage.testDeclarationCount) 'Actual compiled source declaration union differs from plan.'
    }
    Check ($unreviewedTools.Count -eq 0) ('Tool changes require exact explicit hash review: '+[string]::Join('; ',[string[]]@($unreviewedTools)))
    $result=[ordered]@{schemaVersion=1;accepted=$complete;fullCoverage=$complete;metadataOnly=$true;planPath=$planFile;planSha256=$planHash
        originalSourceCount=$original.Count;sharedSourceCount=$shared.Count;nonsharedSourceCount=$nonsharedOwner.Count;actualSourceUnionCount=$actualUnion.Count
        sourceDeclarationCount=$declarationUnion.Count;actualTestMethodCount=$methodOwner.Count;testCases=$totals;allExecutedPassedWithoutSkips=($complete -and $totals.failed -eq 0 -and $totals.notExecuted -eq 0)
        coverageScope='Original Compile and metadata-discoverable method union; actual Results outcomes retained. Full coverage does not count skipped cases as passed.'
        mapping='Source declarations group namespace|top-level root + method name/generic arity/parameter count; nested types map to that root. Discovery uses nonabstract closed classes and public inherited Fact/Theory-derived methods; theory cases collapse only within their partition.'
        originalMetadataCounts=@{projectReferences=@($baseline.Items.ProjectReference).Count;resources=@($baseline.Items.EmbeddedResource).Count;analyzers=@($baseline.Items.Analyzer).Count}
        missingPartitions=@($missingPartitions.ToArray());supersededTestsRuns=@($superseded.ToArray());toolChanges=@($toolChanges.ToArray());partitions=@($partitions.ToArray());issues=@()}
} catch {
    $issues.Add($_.Exception.Message)
    $result.issues=@($issues.ToArray())
    $result['partitions']=@($partitions.ToArray())
    if($null -ne (Get-Variable toolChanges -ErrorAction SilentlyContinue)){$result['toolChanges']=@($toolChanges.ToArray())}
}
$outputFile=[IO.Path]::GetFullPath($OutputPath)
Check ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($outputFile))) 'Output parent must already be a registered task directory.'
[IO.File]::WriteAllText($outputFile,($result|ConvertTo-Json -Depth 18),[Text.UTF8Encoding]::new($false))
Write-Output (@{accepted=$result.accepted;fullCoverage=$result.fullCoverage;outputPath=$outputFile;issues=$result.issues}|ConvertTo-Json -Compress)
if(-not $result.accepted){exit 1}

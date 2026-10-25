#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output, [switch]$AllowInProgress)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$versions = Get-Content (Join-Path $PSScriptRoot 'analyzers.json') -Raw | ConvertFrom-Json -AsHashtable
$projects = @(Get-Content (Join-Path $Output 'projects.txt'))
$rows = [Collections.Generic.List[object]]::new()
$coverage = [Collections.Generic.List[object]]::new()
$acceptedSarif=[Collections.Generic.List[string]]::new()
$toolVersions = @{ SDK=$versions.sdkVersion; Compiler=$versions.sdkVersion; CodeQL=$versions.codeqlVersion }
foreach ($package in $versions.packages) { $toolVersions[$package.tool]=$package.version }
$toolNames = @('SDK','Threading','Meziantou','Roslynator','Sonar','IDisposable','Compiler')
$droppedTools=@($versions.packages | Where-Object { $_.ContainsKey('disabledReason') } | ForEach-Object tool)
$toolNames=@($toolNames | Where-Object { $_ -notin $droppedTools })

function Get-Tool([string]$Rule) {
    if ($Rule -match '^CA\d') { return 'SDK' }
    foreach ($package in $versions.packages) { if ($Rule -match ('^' + $package.prefix + '\d')) { return $package.tool } }
    return 'Compiler'
}
function Get-Scope([string]$Project) {
    if ($Project -match '^tests/|\.Tests/|\.Tests\.csproj$') { return 'test' }
    if ($Project -match '^src/Development/') { return 'development' }
    return 'product'
}
function Get-Relative([string]$Path) {
    if ($Path.StartsWith('file:',[StringComparison]::OrdinalIgnoreCase)) { $Path=([uri]$Path).LocalPath }
    $Path=[uri]::UnescapeDataString($Path).Replace('\','/')
    $root=$repository.Replace('\','/') + '/'
    if ($Path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { return $Path.Substring($root.Length) }
    return $Path
}
function Write-Json([object]$Value, [string]$Path) {
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    # SARIF is the shareable artifact. Local logs are not published. Remove both
    # URI and Windows representations of checkout/cache identity information.
    foreach ($root in @(([uri]($repository + '\')).AbsoluteUri, $repository.Replace('\','/'), $repository.Replace('\','\\'))) {
        $json = $json.Replace($root.TrimEnd('/','\') + '/', '')
        $json = $json.Replace($root.TrimEnd('/','\') + '\\', '')
    }
    $json = $json.Replace($env:USERPROFILE.Replace('\','\\'), 'USER_PROFILE')
    $json = $json.Replace($env:USERPROFILE.Replace('\','/'), 'USER_PROFILE')
    if ($env:USERNAME) { $json = [regex]::Replace($json,'(?i)(?<![\p{L}\p{N}_])' + [regex]::Escape($env:USERNAME) + '(?![\p{L}\p{N}_])','REDACTED_USER') }
    if ($env:COMPUTERNAME) { $json = [regex]::Replace($json,'(?i)(?<![\p{L}\p{N}_])' + [regex]::Escape($env:COMPUTERNAME) + '(?![\p{L}\p{N}_])','REDACTED_MACHINE') }
    [IO.File]::WriteAllText($Path,$json,[Text.UTF8Encoding]::new($false))
}
function Normalize-SarifRun([object]$Run) {
    $queue=[Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($Run)
    $nodes=0
    while($queue.Count) {
        if(++$nodes -gt 750000) { throw 'SARIF normalization exceeded the node bound.' }
        $current=$queue.Dequeue()
        if($current -is [Collections.IDictionary]) {
            $current.Remove('originalUriBaseIds') | Out-Null
            $current.Remove('uriBaseId') | Out-Null
            if($current.Contains('uri') -and [string]$current['uri'] -like 'file:*') {
                $relative=Get-Relative ([string]$current['uri'])
                $current['uri']=if($relative){(@($relative.Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')}else{'.'}
            }
            foreach($value in $current.Values) {
                if($value -is [Collections.IDictionary] -or ($value -is [Collections.IList])) { $queue.Enqueue($value) }
            }
        } elseif($current -is [Collections.IList]) {
            foreach($value in $current) { if($value -is [Collections.IDictionary] -or $value -is [Collections.IList]) { $queue.Enqueue($value) } }
        }
    }
}
function Add-Result([object]$Result,[string]$Project,[string]$Tool,[object]$Rules) {
    $id=[string]$Result.ruleId
    $rule = if ($Rules.ContainsKey($id)) { $Rules[$id] } else { @{} }
    $location = if ($Result['locations'] -and $Result['locations'].Count) { $Result['locations'][0]['physicalLocation'] } else { @{} }
    $file = if ($location['artifactLocation']) { Get-Relative ([string]$location['artifactLocation']['uri']) } else { '' }
    $line = if ($location['region'] -and $location['region']['startLine']) { [int]$location['region']['startLine'] } else { 0 }
    $severity = if ($Result['level']) { [string]$Result['level'] } elseif ($rule['defaultConfiguration'] -and $rule['defaultConfiguration']['level']) { [string]$rule['defaultConfiguration']['level'] } else { 'warning' }
    $message = if ($Result['message']['text']) { $Result['message']['text'] } else { $Result['message']['markdown'] }
    $category = if ($rule['properties'] -and $rule['properties']['category']) { [string]$rule['properties']['category'] } else { '' }
    $title = if ($rule['shortDescription'] -and $rule['shortDescription']['text']) { [string]$rule['shortDescription']['text'] } else { [string]$rule['name'] }
    $sourceKind = if (-not $file) { 'tool-or-compiler' } elseif ($file -match '(^|/)(obj|bin|\.local|artifacts)/|\.g\.cs$|^<|^[A-Za-z]:/|^/') { 'generated-or-external' } else { 'first-party' }
    $rows.Add([pscustomobject][ordered]@{ tool=$Tool; rule=$id; severity=$severity; project=$Project; scope=(Get-Scope $Project); sourceKind=$sourceKind; file=$file; line=$line; title=$title; category=$category; helpUri=$rule['helpUri']; message=$message })
}

foreach ($project in $projects) {
    $name = $project.Replace('/','__').Replace('\','__').Replace('.csproj','')
    $path = Join-Path $Output "raw/$name.sarif"
    if (-not (Test-Path $path)) { $coverage.Add(@{project=$project; roslyn='missing'}); continue }
    if ($AllowInProgress -and (Get-Item $path).Length -eq 0) { $coverage.Add(@{project=$project; roslyn='in-progress'}); continue }
    $sarif = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
    if ($sarif.version -ne '2.1.0') { throw "Expected SARIF 2.1: $path" }
    $coverage.Add(@{project=$project; roslyn='sarif-present'})
    $run = $sarif.runs[0]
    Normalize-SarifRun $run
    $rules=@{}
    foreach ($rule in $run.tool.driver.rules) { $rules[$rule.id]=$rule }
    foreach ($result in $run.results) {
        if ((Get-Tool $result.ruleId) -in $droppedTools) { continue }
        if ($result.ruleId -eq 'AD0001' -and $result.message.text -match 'IDisposableAnalyzers\.' -and 'IDisposable' -in $droppedTools) { continue }
        Add-Result $result $project (Get-Tool $result.ruleId) $rules
    }
    foreach ($tool in $toolNames) {
        $results = @($run.results | Where-Object {
            (Get-Tool $_.ruleId) -eq $tool -and -not ($_.ruleId -eq 'AD0001' -and $_.message.text -match 'IDisposableAnalyzers\.' -and 'IDisposable' -in $droppedTools)
        })
        $selectedRules = @($run.tool.driver.rules | Where-Object { (Get-Tool $_.id) -eq $tool })
        foreach ($result in $results) { $result.Remove('ruleIndex') | Out-Null }
        $splitRun = [ordered]@{tool=@{driver=@{name=$tool; version=$toolVersions[$tool]; rules=$selectedRules}}; results=$results}
        Write-Json @{version='2.1.0'; '$schema'='https://json.schemastore.org/sarif-2.1.0.json'; runs=@($splitRun)} (Join-Path $Output "sarif/$name.$tool.sarif")
        $acceptedSarif.Add("sarif/$name.$tool.sarif")
    }
}
$codeqlPath = Join-Path $Output 'raw/CodeQL.sarif'
if (Test-Path $codeqlPath) {
    $sarif = Get-Content $codeqlPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($run in $sarif.runs) {
        Normalize-SarifRun $run
        $rules=@{}
        foreach ($rule in @($run['tool']['driver']['rules'])) { if ($rule) { $rules[$rule.id]=$rule } }
        if ($run['tool']['extensions']) {
            foreach ($extension in $run['tool']['extensions']) {
                # CodeQL also emits informational extensions without rules.
                foreach ($rule in @($extension['rules'])) { if ($rule) { $rules[$rule.id]=$rule } }
            }
        }
        $byProject=@{}
        foreach ($result in $run.results) {
            $file=Get-Relative ([string]$result.locations[0].physicalLocation.artifactLocation.uri)
            # The most-specific containing project owns the source; linked files
            # remain diagnostic occurrences of their compilation in Roslyn.
            $owner='unmapped'
            $length=-1
            foreach ($project in $projects) {
                $dir=$project.Substring(0,$project.LastIndexOf('/')+1)
                if ($file.StartsWith($dir,[StringComparison]::OrdinalIgnoreCase) -and $dir.Length -gt $length) { $owner=$project; $length=$dir.Length }
            }
            Add-Result $result $owner 'CodeQL' $rules
            if (-not $byProject.ContainsKey($owner)) { $byProject[$owner]=[Collections.Generic.List[object]]::new() }
            $byProject[$owner].Add($result)
        }
        foreach ($project in @($projects | Where-Object { $_ -notmatch '^tests/Validation/NonAdaptedOptimizationLab/' }) + @('unmapped')) {
            $name=$project.Replace('/','__').Replace('\','__').Replace('.csproj','')
            $copy = $run.Clone()
            $copy.results = if ($byProject.ContainsKey($project)) { $byProject[$project].ToArray() } else { @() }
            Write-Json @{version='2.1.0'; '$schema'=$sarif.'$schema'; runs=@($copy)} (Join-Path $Output "sarif/$name.CodeQL.sarif")
            $acceptedSarif.Add("sarif/$name.CodeQL.sarif")
        }
    }
}
Write-Json $acceptedSarif.ToArray() (Join-Path $Output 'accepted-sarif.json')
Write-Json @($rows.ToArray()) (Join-Path $Output 'findings.json')
# Export the already sanitized inventory, so external-source paths cannot expose
# a local profile in the CSV while being redacted only in JSON/SARIF.
Get-Content (Join-Path $Output 'findings.json') -Raw | ConvertFrom-Json | Export-Csv (Join-Path $Output 'findings.csv') -NoTypeInformation -Encoding utf8
$counts = @($rows.ToArray() | Group-Object tool,rule,severity,project,scope,sourceKind | ForEach-Object {
    $first=$_.Group[0]
    [ordered]@{tool=$first.tool; rule=$first.rule; severity=$first.severity; project=$first.project; scope=$first.scope; sourceKind=$first.sourceKind; count=$_.Count}
})
$analyzerFailures=@($rows.ToArray() | Where-Object { $_.rule -match '^(AD0001|CS8032|CS8034|CS9057)$' })
Write-Json @{total=$rows.Count; counts=$counts; coverage=$coverage.ToArray(); analyzerFailures=$analyzerFailures; droppedAnalyzers=@($versions.packages | Where-Object { $_.ContainsKey('disabledReason') })} (Join-Path $Output 'summary.json')
$samples=[Collections.Generic.List[object]]::new()
foreach ($group in ($rows.ToArray() | Where-Object { $_.scope -eq 'product' -and $_.sourceKind -eq 'first-party' } | Group-Object tool,rule)) {
    $ordered=@($group.Group | Sort-Object project,file,line)
    # Round-robin projects, then spread each project's samples across its files.
    $buckets=@($ordered | Group-Object project)
    $sampleSize=if ($ordered.Count -ge 100) { 20 } else { $ordered.Count }
    $chosen=[Collections.Generic.List[object]]::new()
    $indices=@{}
    for ($i=0; $chosen.Count -lt $sampleSize; $i++) {
        $bucket=$buckets[$i % $buckets.Count]
        $index=if ($indices.ContainsKey($bucket.Name)) { $indices[$bucket.Name] } else { 0 }
        $perBucket=[Math]::Min($bucket.Count,$sampleSize)
        if ($index -ge $perBucket) { continue }
        $sampleIndex=[int][Math]::Floor($index * $bucket.Count / $perBucket)
        $chosen.Add($bucket.Group[$sampleIndex]); $indices[$bucket.Name]=$index+1
    }
    $samples.Add(@{tool=$ordered[0].tool; rule=$ordered[0].rule; title=$ordered[0].title; category=$ordered[0].category; total=$ordered.Count; samples=$chosen.ToArray()})
}
Write-Json @($samples.ToArray()) (Join-Path $Output 'triage-samples.json')
Write-Host "Summary: $($rows.Count) diagnostic occurrences; $($projects.Count) projects."

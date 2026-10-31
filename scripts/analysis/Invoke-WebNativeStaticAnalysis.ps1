[CmdletBinding()]
param(
    [ValidateSet('ESLint','Knip','Cppcheck','PowerShell','Zig','CodeQL')][string[]]$Tools = @('ESLint','Knip','Cppcheck','PowerShell','Zig','CodeQL'),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$')][string]$RunName = 'static-analysis-web-queued',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$')][string]$CodeQLRunName = 'static-analysis-web-codeql-queued',
    # Defaults to <ToolsRoot>/CodeQL/2.27.2/codeql/codeql.exe (scripts/dev/MachineRoots.psm1).
    [string]$CodeQL,
    # Defaults to <ToolsRoot>/JobQueue/JobQueue.ps1.
    [string]$Queue,
    [switch]$KeepOpen
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location -LiteralPath $repo
$policy = Join-Path $PSScriptRoot 'workspace-policy.json'
if (-not $CodeQL -or -not $Queue) {
    Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
    $toolsRoot = Get-MachineRoot -Name ToolsRoot
    if (-not $CodeQL) { $CodeQL = Join-Path $toolsRoot 'CodeQL/2.27.2/codeql/codeql.exe' }
    if (-not $Queue) { $Queue = Join-Path $toolsRoot 'JobQueue/JobQueue.ps1' }
}
$pwsh = (Get-Command pwsh -CommandType Application).Source
$node = (Get-Command node -CommandType Application).Source
$manager = Join-Path $repo 'scripts/maintenance/Manage-GeneratedWorkspace.ps1'
$output = Join-Path $repo ".local/$RunName"
$dbRoot = Join-Path $repo ".local/$CodeQLRunName"
if ($RunName -eq $CodeQLRunName) { throw 'Results and databases require separate task names' }
$statuses = [System.Collections.Generic.List[object]]::new()
if (Test-Path -LiteralPath "$output/tool-status.json") { foreach ($old in @(Get-Content -LiteralPath "$output/tool-status.json" -Raw | ConvertFrom-Json)) { if ($old.tool -notin $Tools -and $old.tool -ne 'Summary') { $statuses.Add($old) } } }
function Register-Task([string]$Path, [string]$Purpose) {
    if (Test-Path -LiteralPath $Path) {
        $state = & $manager -Action Inspect -RootId rm-local -WorkspacePath $Path -PolicyPath $policy | ConvertFrom-Json
        if ($state.state -ne 'running') { throw "Use a new RunName; task is not running: $Path" }
    } else {
        $created = & $manager -Action New -RootId rm-local -WorkspacePath $Path -Purpose $Purpose -PolicyPath $policy | ConvertFrom-Json
        if (-not $created -or $created.workspace.state -ne 'running') { throw "Task registration failed: $Path" }
    }
}
function Invoke-Tool([string]$Exe, [string[]]$Arguments, [string]$Log, [int[]]$Accept = @(0), [string]$WorkingDirectory = $repo, [switch]$PreserveStdout) {
    # Each step owns a fresh serial queue slot and a 4 GiB ceiling; never nest this entry in the queue.
    & $queue -Action Run -Label "rm web: $([IO.Path]::GetFileNameWithoutExtension($Log))" -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -MemoryLimitGiB 4 *> "$Log.queue.log"
    $code = $LASTEXITCODE
    $last = Get-Content -LiteralPath "$Log.queue.log" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1
    try { $result = $last | ConvertFrom-Json } catch { throw "Queue did not return a result; see $([IO.Path]::GetFileName($Log)).queue.log" }
    foreach ($pair in @(@($result.log,$Log),@($result.errorLog,"$Log.stderr.log"))) {
        if (Test-Path -LiteralPath $pair[0]) {
            $content = [IO.File]::ReadAllText($pair[0])
            foreach ($path in @($repo,[Environment]::GetFolderPath('UserProfile'))) {
                if ($PreserveStdout -and $pair[1] -eq $Log) { continue }
                if ($path) { $content = $content.Replace($path.Replace('\','\\'),'<local-path>').Replace($path,'<local-path>').Replace($path.Replace('\','/'),'<local-path>') }
            }
            [IO.File]::WriteAllText($pair[1],$content,[Text.UTF8Encoding]::new($false))
        }
    }
    [pscustomobject]@{status=$result.status;exitCode=$result.exitCode;memoryLimitGiB=4;arguments=$Arguments;startedUtc=$result.startedUtc;endedUtc=$result.endedUtc} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Log.result.json"
    if ($result.status -ne 'done' -or $code -notin $Accept) { throw "Queued tool exit $code ($($result.status)); see $([IO.Path]::GetFileName($Log)).stderr.log; memory ceiling 4 GiB, no automatic retry" }
}
$script:nodeToolsReady = $false
$script:nodeRestoreFailure = $null
function Restore-AnalysisTools {
    if ($script:nodeToolsReady) { return }
    if ($script:nodeRestoreFailure) { throw $script:nodeRestoreFailure }
    $missing = @('eslint','knip','typescript-eslint','eslint-plugin-solid','typescript','@microsoft/eslint-formatter-sarif') |
        Where-Object { -not (Test-Path -LiteralPath "$PSScriptRoot/node/node_modules/$_/package.json") }
    if (@($missing).Count) {
        $restore = if (Test-Path -LiteralPath "$PSScriptRoot/node/package-lock.json") { 'ci' } else { 'install' }
        Invoke-Tool $node @('C:/Program Files/nodejs/node_modules/npm/bin/npm-cli.js',$restore,'--prefix',"$PSScriptRoot/node",'--ignore-scripts','--no-audit','--no-fund','--fetch-retries=0','--fetch-timeout=60000','--prefer-offline') "$output/npm-restore.log"
    }
    Invoke-Tool $node @("$PSScriptRoot/node/run-eslint.mjs",$output,'--verify-only') "$output/node-versions.log"
    $script:nodeToolsReady = $true
}
Register-Task $output 'Pinned static analysis output for Claude review'
$artifactMap = @{
    ESLint=@('eslint.sarif'); Knip=@('knip.json','knip-product.json'); Cppcheck=@('cppcheck/cppcheck.sarif')
    PowerShell=@('powershell/findings.sarif'); Zig=@('zig.json'); CodeQL=@('codeql-javascript.sarif','codeql-cpp.sarif')
}
$accepted = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath "$output/accepted-results.json") {
    foreach ($file in @(Get-Content -LiteralPath "$output/accepted-results.json" -Raw | ConvertFrom-Json)) { [void]$accepted.Add([string]$file) }
}
foreach ($selected in $Tools) { foreach ($file in $artifactMap[$selected]) { [void]$accepted.Remove($file) } }
function Save-AcceptedResults { ConvertTo-Json -InputObject @($accepted | Sort-Object) | Set-Content -LiteralPath "$output/accepted-results.json" }
function Accept-Results([string[]]$Files) {
    foreach ($file in $Files) {
        if ($file.EndsWith('.sarif')) {
            $sarif = Get-Content -LiteralPath (Join-Path $output $file) -Raw | ConvertFrom-Json
            if ($sarif.version -ne '2.1.0') { throw "Invalid SARIF version: $file" }
        }
        [void]$accepted.Add($file)
    }
}
Save-AcceptedResults
$hashPaths = @('src/UI/Frontend/package.json','src/UI/Frontend/package-lock.json','src/UI/Frontend/tsconfig.json','src/UI/Frontend/vite.config.ts')
$before = @($hashPaths | ForEach-Object { [pscustomobject]@{file=$_;sha256=(Get-FileHash -LiteralPath $_).Hash} })
foreach ($tool in $Tools) {
    try {
        switch ($tool) {
            'ESLint' {
                foreach ($required in @('src/UI/Frontend/node_modules/solid-js/package.json','src/UI/Frontend/src/preferences/generated/frontendPoints.values.ts')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $repo $required))) { throw "Required frontend type input missing: $required" }
                }
                try { Restore-AnalysisTools } catch { $script:nodeRestoreFailure = $_.Exception.Message; throw }
                Invoke-Tool $node @("$PSScriptRoot/node/run-eslint.mjs",$output) "$output/eslint.log"
            }
            'Knip' {
                try { Restore-AnalysisTools } catch { $script:nodeRestoreFailure = $_.Exception.Message; throw }
                Invoke-Tool $pwsh @('-NoProfile','-File',"$PSScriptRoot/Invoke-KnipAnalysis.ps1",'-RepoRoot',$repo,'-OutputRoot',$output) "$output/knip-run.log"
            }
            'Cppcheck' { Invoke-Tool $pwsh @('-NoProfile','-File',"$PSScriptRoot/Invoke-CppcheckAnalysis.ps1",'-RepoRoot',$repo,'-OutputRoot',"$output/cppcheck") "$output/cppcheck-run.log" }
            'PowerShell' { Invoke-Tool $pwsh @('-NoProfile','-File',"$PSScriptRoot/Invoke-PowerShellAnalysis.ps1",'-RepoRoot',$repo,'-OutputRoot',"$output/powershell") "$output/powershell-run.log" }
            'Zig' { Invoke-Tool $pwsh @('-NoProfile','-File',"$PSScriptRoot/Invoke-ZigAnalysis.ps1",'-RepoRoot',$repo,'-OutputRoot',$output) "$output/zig.log" }
            'CodeQL' {
                if (-not (Test-Path -LiteralPath $CodeQL)) { throw 'CodeQL 2.27.2 bundle missing' }
                Invoke-Tool $CodeQL @('version','--format=json') "$output/codeql-version.log"
                $version = Get-Content -LiteralPath "$output/codeql-version.log" -Raw | ConvertFrom-Json
                if ($version.version -ne '2.27.2') { throw 'CodeQL version mismatch' }
                Register-Task $dbRoot 'Disposable CodeQL databases; deliver SARIF before Complete'
                $deliveries = Join-Path $dbRoot 'deliveries'
                [void][IO.Directory]::CreateDirectory($deliveries)
                $dbOutcome = 'success'
                try {
                    foreach ($language in @('javascript','cpp')) {
                        $codeqlLanguage = if ($language -eq 'javascript') { 'javascript-typescript' } else { 'cpp' }
                        $db = Join-Path $dbRoot $language
                        Invoke-Tool $CodeQL @('database','create',$db,"--language=$codeqlLanguage",'--build-mode=none',"--source-root=$repo", "--codescanning-config=$PSScriptRoot/codeql-$language.yml",'--threads=2','--ram=3072') "$output/codeql-$language-create.log"
                        $rawSarif = Join-Path $deliveries "$language.raw.sarif"
                        Invoke-Tool $CodeQL @('database','analyze',$db,"codeql/${language}-queries:codeql-suites/${language}-security-and-quality.qls",'--format=sarifv2.1.0',"--output=$rawSarif",'--threads=2','--ram=3072') "$output/codeql-$language-analyze.log"
                        $scopedLog = "$output/codeql-$language-scope.log"
                        Invoke-Tool $node @("$PSScriptRoot/node/filter-codeql.mjs",$rawSarif,'-',$language) $scopedLog -PreserveStdout
                        $scoped = Get-Content -LiteralPath $scopedLog -Raw | ConvertFrom-Json
                        if ($scoped.version -ne '2.1.0') { throw 'Scoped SARIF is invalid' }
                        $hash = (Get-FileHash -LiteralPath $scopedLog).Hash
                        Move-Item -LiteralPath $scopedLog -Destination "$output/codeql-$language.sarif" -ErrorAction Stop
                        $receipt = Get-Content -LiteralPath "$scopedLog.stderr.log" | Where-Object { $_.StartsWith('{"language"') } | Select-Object -First 1
                        $receipt | ConvertFrom-Json | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$output/codeql-$language.sarif.scope.json"
                        if ((Get-FileHash -LiteralPath "$output/codeql-$language.sarif").Hash -ne $hash) { throw 'SARIF delivery hash changed' }
                        Accept-Results @("codeql-$language.sarif")
                        Save-AcceptedResults
                    }
                } catch { $dbOutcome='failed'; throw } finally {
                    & $manager -Action Complete -RootId rm-local -WorkspacePath $dbRoot -Outcome $dbOutcome -PolicyPath $policy | Set-Content -LiteralPath "$output/codeql-lifecycle.json"
                    & $manager -Action Prune -RootId rm-local -WorkspacePaths @($dbRoot) -PolicyPath (Join-Path $PSScriptRoot 'disposable-workspace-policy.json') -Apply | Set-Content -LiteralPath "$output/codeql-prune.json"
                }
            }
        }
        Accept-Results $artifactMap[$tool]
        $statuses.Add([pscustomobject]@{tool=$tool;status='complete';detail=''})
    } catch {
        $statuses.Add([pscustomobject]@{tool=$tool;status='failed';detail=$_.Exception.Message.Replace($repo,'.')})
        Write-Warning "$tool failed: $($_.Exception.Message)"
    } finally {
        Save-AcceptedResults
        $statuses | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$output/tool-status.json"
    }
}
try {
    Invoke-Tool $node @("$PSScriptRoot/node/summarize.mjs",$output,'--stdout') "$output/summary.log" -PreserveStdout
    $summary = Get-Content -LiteralPath "$output/summary.log" -Raw | ConvertFrom-Json
    $summary.findings | ConvertTo-Json -Depth 10 -AsArray | Set-Content -LiteralPath "$output/findings.json"
    $summary.summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$output/summary.json"
    [IO.File]::WriteAllText("$output/summary.md",$summary.markdown,[Text.UTF8Encoding]::new($false))
} catch {
    $statuses.Add([pscustomobject]@{tool='Summary';status='failed';detail=$_.Exception.Message.Replace($repo,'.')})
}
$after = @($hashPaths | ForEach-Object { [pscustomobject]@{file=$_;sha256=(Get-FileHash -LiteralPath $_).Hash} })
[pscustomobject]@{before=$before;after=$after;unchanged=(@(Compare-Object ($before | ConvertTo-Json) ($after | ConvertTo-Json)).Count -eq 0)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$output/build-input-proof.json"
$statuses | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$output/tool-status.json"
if (-not $KeepOpen) {
    $outcome = if (@($statuses | Where-Object status -eq 'failed').Count) { 'failed' } else { 'success' }
    & $manager -Action Complete -RootId rm-local -WorkspacePath $output -Outcome $outcome -PolicyPath $policy | Out-Null
}
if (@($statuses | Where-Object status -eq 'failed').Count) { exit 1 }







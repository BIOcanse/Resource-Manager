#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName = ('static-analysis-dotnet-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')),
    [string]$PackageRoot = (Join-Path $env:USERPROFILE '.nuget/packages'),
    [string]$CodeQL = 'D:\Code\CommonAssets\Tools\CodeQL\2.27.2\codeql\codeql.exe',
    [switch]$Resume,
    [switch]$RoslynOnly,
    [switch]$CodeQLOnly,
    [switch]$SkipCodeQL,
    [switch]$RestoreAnalyzers,
    [switch]$UseExistingCodeQL,
    [switch]$AllowMissingAnalyzers
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repository ".local/$RunName"
$maintenance = Join-Path $repository 'scripts/maintenance/Manage-GeneratedWorkspace.ps1'
$versions = Get-Content (Join-Path $PSScriptRoot 'analyzers.json') -Raw | ConvertFrom-Json
. (Join-Path $PSScriptRoot 'StaticAnalysis.Queue.ps1')
. (Join-Path $PSScriptRoot 'StaticAnalysis.Packages.ps1')
$oldEnvironment = @{}
foreach ($key in @('RM_LEDGER_WORK_ROOT','DOTNET_CLI_HOME','DOTNET_NOLOGO','VSLANG','DOTNET_CLI_UI_LANGUAGE','RestorePackagesPath','NuGetAudit')) {
    $oldEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
$failure = $null
$registered = $false
$buildRegistered = $false
$buildRoot = Join-Path $repository ('.local/' + $RunName + '-build-' + [DateTime]::UtcNow.ToString('HHmmss'))
# Keep the same canonical rm-local owner and finite limits. Other worktrees/volumes
# are outside this entry's ownership (and can be inaccessible in a worker sandbox).
$policy = Get-Content (Join-Path $repository 'scripts/maintenance/generated-workspace-policy.json') -Raw | ConvertFrom-Json
$policy.roots = @($policy.roots | Where-Object id -eq 'rm-local')
$policy.referenceRoots = @()
[IO.Directory]::CreateDirectory((Join-Path $repository '.local')) | Out-Null
$policyPath = Join-Path $repository '.local/static-analysis-policy.json'
$policy | ConvertTo-Json -Depth 12 | Set-Content $policyPath -Encoding utf8

function Invoke-Lifecycle([string]$Action, [string]$Path, [string]$Purpose = '', [string]$Outcome = '') {
    $parameters = @{ Action=$Action; RootId='rm-local'; WorkspacePath=$Path; PolicyPath=$policyPath }
    if ($Purpose) { $parameters.Purpose=$Purpose }
    if ($Outcome) { $parameters.Outcome=$Outcome }
    $global:LASTEXITCODE = 0
    & $maintenance @parameters | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle $Action failed for $Path" }
}

function Invoke-Dotnet([string[]]$Arguments, [string]$Log) {
    return (Invoke-StaticAnalysisQueued -FilePath (Get-Command dotnet).Source -Arguments $Arguments -Log $Log `
        -Label ('rm C# ' + [IO.Path]::GetFileNameWithoutExtension($Log)) -Repository $repository)
}

try {
    Set-Location $repository
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $env:VSLANG = '1033'
    $env:DOTNET_CLI_HOME = Join-Path $env:LOCALAPPDATA 'ResourceManagerStaticAnalysis/dotnet'
    # Exec-launched frontend builds are independent MSBuild invocations. Carry
    # the same package cache explicitly through that process boundary as well.
    $env:RestorePackagesPath = [IO.Path]::GetFullPath($PackageRoot)
    $env:NuGetAudit = 'false'
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -ne $versions.sdkVersion) { throw "Expected SDK $($versions.sdkVersion), found $sdk" }
    if ($Resume) {
        $state = Get-Content (Join-Path $output '.generated-workspace.json') -Raw | ConvertFrom-Json
        if ($state.state -ne 'running' -or $state.workspacePath -ine $output) { throw 'Resume requires the exact running registered run.' }
    } else {
        Invoke-Lifecycle New $output 'C# static analysis: pinned analyzers, SARIF and triage evidence'
    }
    $registered = $true
    foreach ($directory in @('raw','sarif','logs')) { [IO.Directory]::CreateDirectory((Join-Path $output $directory)) | Out-Null }
    $statusPath = Join-Path $output 'status.json'
    $status = if ($Resume -and (Test-Path $statusPath)) { Get-Content $statusPath -Raw | ConvertFrom-Json -AsHashtable } else {
        @{ sdk=$sdk; roslyn=@(); missingAnalyzers=@(); codeql='pending'; complete=$false }
    }
    if (-not $CodeQLOnly) {
        $status.missingAnalyzers=@()
        $status.droppedAnalyzers=@($versions.packages | Where-Object { $_.PSObject.Properties['disabledReason'] } | ForEach-Object { @{id=$_.id; version=$_.version; reason=$_.disabledReason} })
        if ($RestoreAnalyzers) {
            $restoreProject = [Xml.XmlDocument]::new()
            $node = $restoreProject.CreateElement('Project'); $node.SetAttribute('Sdk','Microsoft.NET.Sdk'); [void]$restoreProject.AppendChild($node)
            $properties = $restoreProject.CreateElement('PropertyGroup'); [void]$node.AppendChild($properties)
            $framework = $restoreProject.CreateElement('TargetFramework'); $framework.InnerText='net10.0'; [void]$properties.AppendChild($framework)
            $items = $restoreProject.CreateElement('ItemGroup'); [void]$node.AppendChild($items)
            foreach ($package in $versions.packages) {
                if ($package.PSObject.Properties['disabledReason']) { continue }
                $item=$restoreProject.CreateElement('PackageReference'); $item.SetAttribute('Include',$package.id)
                $item.SetAttribute('Version',('[' + $package.version + ']')); $item.SetAttribute('PrivateAssets','all'); [void]$items.AppendChild($item)
            }
            $restorePath=Join-Path $output 'AnalyzerRestore.csproj'
            $restoreProject.Save($restorePath)
            $restoreExit=Invoke-Dotnet @('restore',$restorePath,'--packages',$PackageRoot,'--source','https://api.nuget.org/v3/index.json',
                '-p:NuGetAudit=false',"-p:MSBuildProjectExtensionsPath=$output/package-restore/") (Join-Path $output 'logs/analyzer-restore.log')
            if ($restoreExit -ne 0) { throw 'Analyzer package preparation failed; see analyzer-restore.log.' }
        }
        $loaded = [Collections.Generic.List[object]]::new()
        $dllPaths = [Collections.Generic.List[string]]::new()
        foreach ($package in $versions.packages) {
            if ($package.PSObject.Properties['disabledReason']) { continue }
            $packagePath = Join-Path $PackageRoot ($package.id.ToLowerInvariant() + '/' + $package.version)
            $analyzers = Join-Path $packagePath 'analyzers'
            if (-not (Test-Path $analyzers)) {
                $status.missingAnalyzers += "$($package.id)@$($package.version)"
                continue
            }
            # Package-owned analyzer tree is small; bounded iterative enumeration.
            $queue = [Collections.Generic.Queue[string]]::new()
            $queue.Enqueue($analyzers)
            $nodes=0
            while ($queue.Count) {
                foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue()) {
                    if (++$nodes -gt 10000) { throw 'Analyzer package enumeration bound exceeded.' }
                    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected analyzer package reparse point.' }
                    if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) }
                    elseif ($item.Extension -eq '.dll' -and $item.FullName -notmatch '\\(?:vb|resources)\\|CodeFix|\.resources\.dll$') { $dllPaths.Add($item.FullName) }
                }
            }
            $loaded.Add($package)
        }
        if ($status.missingAnalyzers.Count -and -not $AllowMissingAnalyzers) {
            $status | ConvertTo-Json -Depth 12 | Set-Content $statusPath -Encoding utf8
            throw ('Prepopulate exact NuGet analyzer packages: ' + ($status.missingAnalyzers -join ', '))
        }
        $xml = [Xml.XmlDocument]::new()
        $projectNode = $xml.CreateElement('Project'); [void]$xml.AppendChild($projectNode)
        $items = $xml.CreateElement('ItemGroup'); [void]$projectNode.AppendChild($items)
        $sdkRoot=Join-Path (Split-Path -Parent (Get-Command dotnet).Source) "sdk/$sdk"
        $compilerVersion=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $sdkRoot 'Roslyn/bincore/csc.dll')).Version
        $selectedAssemblies=@(Select-StaticAnalysisAssemblies $dllPaths.ToArray() $compilerVersion)
        foreach ($path in $selectedAssemblies) {
            $item = $xml.CreateElement('Analyzer'); $item.SetAttribute('Include',$path); [void]$items.AppendChild($item)
        }
        $xml.Save((Join-Path $output 'AnalyzerReferences.targets'))
        $loaded.ToArray() | ConvertTo-Json -Depth 6 -AsArray | Set-Content (Join-Path $output 'loaded-analyzers.json') -Encoding utf8
        if (Test-Path (Join-Path $buildRoot '.generated-workspace.json')) {
            $buildState = Get-Content (Join-Path $buildRoot '.generated-workspace.json') -Raw | ConvertFrom-Json
            if ($buildState.state -ne 'running') { throw 'Build workspace already completed; use a new RunName.' }
        } else { Invoke-Lifecycle New $buildRoot 'Temporary static analysis build and frontend preferences generator' }
        $buildRegistered = $true
        $env:RM_LEDGER_WORK_ROOT = Join-Path $buildRoot 'ledger'
        & $maintenance -Action References -RootId rm-local -WorkspacePath $output -References @($buildRoot) -PolicyPath $policyPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot register active build dependency.' }
        $projects = @(& git -c core.quotePath=false ls-files -- 'src/**/*.csproj' 'sdk/csharp/**/*.csproj' 'tests/**/*.csproj')
        if ($LASTEXITCODE -ne 0 -or -not $projects.Count) { throw 'Cannot inventory tracked C# projects.' }
        $projects | Set-Content (Join-Path $output 'projects.txt') -Encoding utf8
        $common = @('-c','Release','--disable-build-servers','-maxcpucount:1','-p:BuildInParallel=false',
            "-p:RestorePackagesPath=$PackageRoot",'-p:NuGetAudit=false')
        $baseline = 'src/Shared/ResourceManager.Shared.csproj'
        $normalExit = Invoke-Dotnet (@('build',$baseline,'-t:Rebuild') + $common) (Join-Path $output 'logs/normal-before.log')
        if ($normalExit -ne 0) { throw 'Normal baseline build failed.' }
        # The isolated artifacts path forces fresh compilations without cleaning
        # or modifying existing developer outputs. Referenced projects retain
        # incremental compilation within this one run.
        $analysis = $common + @("--artifacts-path=$buildRoot/artifacts",'-p:ResourceManagerStaticAnalysis=true',
            "-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot/StaticAnalysis.targets",
            "-p:StaticAnalysisOutput=$output","-p:StaticAnalysisRepository=$repository",'-p:TreatWarningsAsErrors=false','-p:WarningsAsErrors=',
            '-p:UseSharedCompilation=false',"-p:ResourceManagerLedgerWorkRoot=$env:RM_LEDGER_WORK_ROOT")
        $status.roslyn = @()
        foreach ($project in $projects) {
            $name = $project.Replace('/','__').Replace('\','__').Replace('.csproj','')
            Write-Host "Analyzing $project"
            $exitCode = Invoke-Dotnet (@('build',$project) + $analysis) (Join-Path $output "logs/$name.log")
            $status.roslyn += @{ project=$project; exitCode=$exitCode; sarif=(Test-Path (Join-Path $output "raw/$name.sarif")) }
            $status | ConvertTo-Json -Depth 12 | Set-Content $statusPath -Encoding utf8
        }
        $normalExit = Invoke-Dotnet (@('build',$baseline,'-t:Rebuild') + $common) (Join-Path $output 'logs/normal-after.log')
        $before = @(Get-Content (Join-Path $output 'logs/normal-before.log') | Where-Object { $_ -match ': warning ' } | Sort-Object -Unique)
        $after = @(Get-Content (Join-Path $output 'logs/normal-after.log') | Where-Object { $_ -match ': warning ' } | Sort-Object -Unique)
        $status.normalBuild = @{ exitCode=$normalExit; sameWarnings=(@(Compare-Object $before $after).Count -eq 0); before=$before; after=$after }
    }
    if ($UseExistingCodeQL) {
        . (Join-Path $PSScriptRoot 'StaticAnalysis.Inputs.ps1')
        $codeqlStatus=Get-Content (Join-Path $output 'logs/codeql-status.json') -Raw | ConvertFrom-Json
        $codeqlInputs=Get-Content (Join-Path $output 'codeql-source-inputs.json') -Raw | ConvertFrom-Json
        $currentInputs=Get-StaticAnalysisInputs $repository
        $hash=(Get-FileHash (Join-Path $output 'raw/CodeQL.sarif') -Algorithm SHA256).Hash
        if ($codeqlStatus.actualVersion -ne $versions.codeqlVersion -or $codeqlStatus.databaseAnalyzeExitCode -ne 0 -or
            $codeqlStatus.failure -or $hash -ne $codeqlStatus.sarifSha256 -or $codeqlInputs.sha256 -ne $currentInputs.sha256) {
            throw 'Existing CodeQL result is incomplete or its tracked source inputs/SARIF have changed.'
        }
        $status.codeql='success'
    } elseif (-not $SkipCodeQL -and -not $RoslynOnly) {
        & (Join-Path $PSScriptRoot 'Invoke-CodeQL.ps1') -Output $output -CodeQL $CodeQL -PolicyPath $policyPath
        $status.codeql = if ($LASTEXITCODE -eq 0) { 'success' } else { 'failed' }
    } elseif ($SkipCodeQL) { $status.codeql = 'explicitly-skipped' }
    $status.complete = (-not $status.missingAnalyzers.Count -and @($status.roslyn | Where-Object { $_.exitCode -ne 0 -or -not $_.sarif }).Count -eq 0 -and $status.codeql -eq 'success')
    $status | ConvertTo-Json -Depth 12 | Set-Content $statusPath -Encoding utf8
    $summaryExit = Invoke-StaticAnalysisQueued -FilePath (Get-Command pwsh).Source -Arguments @('-NoProfile','-File',
        (Join-Path $PSScriptRoot 'Summarize-StaticAnalysis.ps1'),'-Output',$output) -Log (Join-Path $output 'logs/summary.log') `
        -Label 'rm C# static analysis summary' -Repository $repository
    if ($summaryExit -ne 0) { throw 'Summary failed.' }
    $summary=Get-Content (Join-Path $output 'summary.json') -Raw | ConvertFrom-Json
    if ($summary.analyzerFailures.Count) { throw 'An analyzer failed to execute; see summary.json. Diagnostics are not complete.' }
    if (-not $RoslynOnly -and -not $status.complete) { throw 'Analysis incomplete; see status.json and build logs.' }
} catch {
    $failure = $_
    [Console]::Error.WriteLine($_.Exception.Message)
} finally {
    if ($buildRegistered) {
        & $maintenance -Action References -RootId rm-local -WorkspacePath $output -References @() -PolicyPath $policyPath | Out-Null
        Invoke-Lifecycle Complete $buildRoot -Outcome $(if ($failure) { 'failed' } else { 'success' })
    }
    if ($registered -and (-not $RoslynOnly -or $failure)) {
        Invoke-Lifecycle Complete $output -Outcome $(if ($failure) { 'failed' } else { 'success' })
    }
    foreach ($key in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$oldEnvironment[$key],'Process') }
}
if ($failure) { exit 1 }

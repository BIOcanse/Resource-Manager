# Invoked only as a JobQueue payload with -MemoryLimitGiB 4.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Build','Full','FreedomPoint')][string]$Mode,
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Ledger,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('true','false')][string]$StateNames = 'true',
    [string]$BaselineTrx,
    # Defaults to .local\wrapup-fixes-20261023\review-scratch in the checkout of codex/development-mainline.
    [string]$ReviewScratch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $Root).Path
Import-Module (Join-Path $PSScriptRoot '..\dev\MachineRoots.psm1') -DisableNameChecking
$tools = Get-MachineRoot -Name ToolsRoot
if (-not $ReviewScratch) {
    $worktree = $null
    $dev = @(foreach ($line in @(git -C $Root worktree list --porcelain)) {
        if ($line -like 'worktree *') { $worktree = $line.Substring(9) }
        elseif ($line -eq 'branch refs/heads/codex/development-mainline') { [IO.Path]::GetFullPath($worktree) }
    }) | Select-Object -First 1
    if (-not $dev) { throw 'No checkout of codex/development-mainline is registered; pass -ReviewScratch.' }
    $ReviewScratch = Join-Path $dev '.local\wrapup-fixes-20261023\review-scratch'
}
$Ledger = (Resolve-Path -LiteralPath $Ledger).Path
$Output = [IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath (Join-Path $Ledger '.generated-workspace.json'))) { throw 'Use the registered ledger.' }
if (-not $Output.StartsWith((Join-Path $Root '.local\reports\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence must be under this task registered reports directory.'
}
[void][IO.Directory]::CreateDirectory($Output)
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$env:PATH = "C:\Program Files\dotnet;$tools\PowerShell\7.6.5;$tools\Zig\zig-0.16.0;" +
    "$tools\WinGetPackages\BrechtSanders.WinLibs.POSIX.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\mingw64\bin;" +
    'C:\Program Files\nodejs;C:\Program Files\Git\cmd;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0'
$env:NuGetAudit = 'false'
$env:RM_REVIEW_SCRATCH = $ReviewScratch
Set-Location -LiteralPath $Root
$log = Join-Path $Output ($Mode + '.log')
$properties = @('-p:BuildInParallel=false', '-p:UseSharedCompilation=false', '-p:ResourceManagerZigJobs=1',
    "-p:ResourceManagerStateNames=$StateNames", "-p:ResourceManagerLedgerWorkRoot=$Ledger", '-p:NuGetAudit=false')
if ($Mode -in @('Build', 'FreedomPoint')) {
    # Refresh the backend-owned grouping contract before the test-project build:
    # its frontend projection generator rejects a stale contract. The checker
    # builds the backend directly and validates the existing groups via the SDK.
    $check = @{
        RepositoryRoot = $Root
        OutputDirectory = (Join-Path $Output 'source-check')
        AdditionalBuildProperties = $properties
        RefreshPartitionContract = $true
    }
    & (Join-Path $Root 'scripts\validation\Test-FreedomPointSources.ps1') @check *> $log
    $code = $LASTEXITCODE
    if ($Mode -eq 'Build' -and $code -eq 0) {
        & dotnet.exe build 'tests\App\Resource Manager-APP.Tests.csproj' -c Release --disable-build-servers -m:1 @properties *>> $log
        $code = $LASTEXITCODE
    }
} else {
    $trx = Join-Path $Output 'full-backend.trx'
    $arguments = @('test', 'tests\App\Resource Manager-APP.Tests.csproj', '-c', 'Release', '--disable-build-servers', '-m:1') + $properties +
        @('--blame-hang-timeout', '15m', '--blame-hang-dump-type', 'none', '--logger', "trx;LogFileName=$trx")
    & dotnet.exe @arguments *> $log
    $code = $LASTEXITCODE
}
if (Select-String -LiteralPath $log -Pattern '中止|崩溃|aborted' -Quiet) { $code = 1 }
$result = [ordered]@{ mode=$Mode; exitCode=$code; stateNames=$StateNames; log=$log; generatedUtc=[DateTime]::UtcNow.ToString('o') }
if ($Mode -eq 'Full' -and (Test-Path -LiteralPath $trx)) {
    [xml]$document = Get-Content -LiteralPath $trx -Raw
    $counters = $document.TestRun.ResultSummary.Counters
    $result.tests = [ordered]@{ total=[int]$counters.total; passed=[int]$counters.passed; failed=[int]$counters.failed }
    if (-not [string]::IsNullOrWhiteSpace($BaselineTrx)) {
        [xml]$baseline = Get-Content -LiteralPath $BaselineTrx -Raw
        $result.baselineTotal = [int]$baseline.TestRun.ResultSummary.Counters.total
        if ($result.tests.total -lt $result.baselineTotal) { $code = 1; $result.exitCode = 1 }
    }
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'result.json') -Encoding utf8
Get-Content -LiteralPath $log | Select-String -Pattern '(已通过|失败|Passed|Failed)[!:]|error |FreedomPoint:' | Select-Object -Last 10
$result | ConvertTo-Json -Depth 5
exit $code

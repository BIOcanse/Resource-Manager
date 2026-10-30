# Run from the existing effective administrator CONTROL SESSION. Never elevates,
# installs/registers a service, or contacts the installed product.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RegisteredWorkspaceRoot,
    [string]$BuildDirectoryName = 'actual-use-build',
    [string]$RuntimeDirectoryName = 'actual-use-run-001',
    [switch]$FilterOnly,
    [string]$MemoryLimitScript = (Join-Path $PSScriptRoot '..\dev\Invoke-WithMemoryLimit.ps1')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Invoke this script from the existing elevated CONTROL SESSION. The script never elevates itself.'
}
foreach ($name in @($BuildDirectoryName, $RuntimeDirectoryName)) {
    if ($name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}$') { throw 'Directory names must be single child names.' }
}
$owner = [IO.Path]::GetFullPath($RegisteredWorkspaceRoot).TrimEnd('\')
if (-not $owner.StartsWith('F:\', [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.File]::Exists((Join-Path $owner '.generated-workspace.json'))) { throw 'An existing registered task root on F: is required.' }
$build = Join-Path $owner $BuildDirectoryName
if (-not [IO.File]::Exists((Join-Path $build '.generated-workspace.json'))) { throw 'The accepted build must have its own generated workspace lifecycle.' }
$image = Join-Path $build 'image'
$runtime = Join-Path $build $RuntimeDirectoryName
if (Test-Path -LiteralPath $runtime) { throw 'Use a fresh runtime directory name.' }
$manifest = Join-Path $image 'artifact-manifest.json'
$source = Join-Path $image 'source-input-manifest.json'
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$probeExit = 1
$failure = $null
try {
    $bound = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
    $ownPath = [IO.Path]::GetFullPath($PSCommandPath)
    $ownBinding = @($bound.firstPartySources | Where-Object { [string]$_.path -eq $ownPath })
    if ($ownBinding.Count -ne 1 -or (Get-FileHash -LiteralPath $ownPath -Algorithm SHA256).Hash -ne $ownBinding[0].sha256) {
        throw 'This invocation script is not the source-bound reviewed script.'
    }
    # The helper owns the complete child tree with a 4 GiB Job limit.
    & $MemoryLimitScript -FilePath $dotnet -ArgumentList @(
        (Join-Path $image 'ResourceManager.ControlDecisionAcceptance.dll'), $(if ($FilterOnly) { '--gpu-filter-only' } else { '--actual-use' }),
        '--registered-root', $build, '--runtime-root', $runtime,
        '--artifact-manifest', $manifest, '--source-manifest', $source
    ) -LimitGiB 4
    $probeExit = $LASTEXITCODE
    if ($probeExit -ne 0) { $failure = "Copied probe exited $probeExit." }
}
catch { $failure = $_.Exception.ToString() }
finally {
    # Runtime data is part of the supplied registered task, whose lifecycle is
    # owned by its caller; do not close that task or unrelated validation batches.
    [void][IO.Directory]::CreateDirectory($runtime)
    $receiptPath = Join-Path $runtime $(if ($FilterOnly) { 'evidence/gpu-filter-only.json' } else { 'evidence/acceptance.json' })
    $receipt = if (Test-Path -LiteralPath $receiptPath) { Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } else { $null }
    $passed = $probeExit -eq 0 -and $null -eq $failure -and $null -ne $receipt -and $receipt.status -eq 'passed'
    $stateNames = if ($FilterOnly) { @('filtered-no-capture','filtered-capture','filtered-capture-reenabled','unfiltered-baseline','owned-session-cleanup') } else { @('a-no-subscriptions','b-30-second-residency-two-windows','c-tray-smart-with-interrupt-report','gpu-allocation-filter','e-subscriptions-removed','cleanup','owned-session-cleanup') }
    $states = foreach ($name in $stateNames) {
        $found = if ($null -ne $receipt) { @($receipt.cases | Where-Object Name -EQ $name) } else { @() }
        [ordered]@{ name = $name; passed = $found.Count -eq 1 -and $found[0].status -eq 'passed'; detail = $found }
    }
    if (@($states | Where-Object { -not $_.passed }).Count -gt 0) { $passed = $false }
    $verdict = [ordered]@{ contract = 'resource-manager-actual-use-verdict-v2'; status = $(if ($passed) { 'passed' } else { 'failed' });
        generatedUtc = [DateTime]::UtcNow.ToString('o'); image = $image; runtime = $runtime; mode = $(if ($FilterOnly) { 'gpu-filter-only' } else { 'actual-use' }); exitCode = $probeExit;
        artifactManifestSha256 = $(if (Test-Path -LiteralPath $manifest) { (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash } else { $null });
        sourceManifestSha256 = $(if (Test-Path -LiteralPath $source) { (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash } else { $null });
        states = @($states); error = $failure; acceptanceReceipt = $receiptPath }
    $verdict | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runtime $(if ($FilterOnly) { 'gpu-filter-only-verdict.json' } else { 'actual-use-verdict.json' })) -Encoding utf8
    $verdict | ConvertTo-Json -Depth 12
}
if ($passed) { exit 0 }
exit 1

#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('FocusedOn','FocusedOff','Full')][string]$Gate,
    [Parameter(Mandatory)][string]$Ledger,
    # Defaults to .local\wrapup-fixes-20261023\gate-scripts\Queue-FocusedLimited.ps1 in the checkout of codex/development-mainline.
    [string]$QueueScript
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $QueueScript) {
    $worktree = $null
    $dev = @(foreach ($line in @(git -C $root worktree list --porcelain)) {
        if ($line -like 'worktree *') { $worktree = $line.Substring(9) }
        elseif ($line -eq 'branch refs/heads/codex/development-mainline') { [IO.Path]::GetFullPath($worktree) }
    }) | Select-Object -First 1
    if (-not $dev) { throw 'No checkout of codex/development-mainline is registered; pass -QueueScript.' }
    $QueueScript = Join-Path $dev '.local\wrapup-fixes-20261023\gate-scripts\Queue-FocusedLimited.ps1'
}
$classes = @(
    'HardwareMetricPublishOnceTests', 'HardwareMetricNativeCurrentRetentionTests',
    'HardwareMetricCurrentByteAccountingTests', 'HardwareMetricSelectedCurrentReadTests',
    'HardwarePublicationReasonTests', 'HostManagerAdversarialCorrectnessTests',
    'CompiledHostManagerMetricSnapshotPlanTests', 'MetricScratchOwnerLifecycleTests',
    'MetricScratchDirectoryFailureTests', 'MetricScratchServiceGraphTests', 'NativeMetricSnapshotWorkspaceTests',
    'WindowsHardwareMetricSamplerLifecycleTests', 'WindowsHardwareMetricCaptureIsolationTests',
    'WindowsHardwareMetricGpuTopologyIsolationTests', 'MetricCatalogueSubscriptionTests',
    'MetricDeviceFieldTests', 'MetricDeviceFieldAddressTests',
    'MetricSnapshotWireSnapshotTests', 'NativeMetricSnapshotValueSemanticsTests',
    'NativeMetricSnapshotProductionAuthorityTests', 'NativeMetricSnapshotAbiTests',
    'NativeMetricSnapshotObservationProjectionTests', 'HostMemoryProtectionSourceTests',
    'HostMemoryProtectionJoinToolTests', 'HostMemoryProtectionTests', 'HostMemoryProtectionExecutorTests',
    'HostMemoryProtectionTreesTests', 'HostManagerSmartCoordinatorCompositionTests',
    'HostManagerSmartCoordinatorScoreOnlyCompositionTests', 'HostManagerReportCoordinatorOwnerTests',
    'HostManagerReportFactProjectorTests', 'MetricReadingSourceSelectionTests', 'MetricFieldCatalogRebindingTests',
    'ReadWriteCoreIntegrationTests'
)
$filter = if ($Gate -eq 'Full') { 'FullyQualifiedName~.' }
    else { ($classes | ForEach-Object { "FullyQualifiedName~Resource_Manager_APP.Tests.$_" }) -join '|' }
$names = if ($Gate -eq 'FocusedOff') { 'false' } else { 'true' }
& $QueueScript `
    -Root $root -Filter $filter -Label "hardware-publish-$($Gate.ToLowerInvariant())" `
    -Ledger $Ledger -MemoryLimitGiB 4 -StateNames $names
exit $LASTEXITCODE

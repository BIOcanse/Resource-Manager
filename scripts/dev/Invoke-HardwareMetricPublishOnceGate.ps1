#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('FocusedOn','FocusedOff','Full')][string]$Gate,
    [Parameter(Mandatory)][string]$Ledger
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
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
& 'D:\Code\Resource-Manager-Development\.local\wrapup-fixes-20261023\gate-scripts\Queue-FocusedLimited.ps1' `
    -Root $root -Filter $filter -Label "hardware-publish-$($Gate.ToLowerInvariant())" `
    -Ledger $Ledger -MemoryLimitGiB 4 -StateNames $names
exit $LASTEXITCODE

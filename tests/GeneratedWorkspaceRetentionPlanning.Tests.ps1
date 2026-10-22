[CmdletBinding()]
param(
    [string]$ModulePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ModulePath)) {
    $ModulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/maintenance/GeneratedWorkspaceRetention.psm1'
}
$moduleFullPath = [System.IO.Path]::GetFullPath($ModulePath)
if (-not (Test-Path -LiteralPath $moduleFullPath -PathType Leaf)) {
    throw "GeneratedWorkspaceRetention module was not found: $moduleFullPath"
}

Import-Module -Name $moduleFullPath -Force -DisableNameChecking
$script:RetentionModule = Get-Module | Where-Object {
    -not [string]::IsNullOrWhiteSpace([string]$_.Path) -and
    [string]::Equals([System.IO.Path]::GetFullPath($_.Path), $moduleFullPath, [System.StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
if ($null -eq $script:RetentionModule) {
    throw "Imported module could not be resolved by path: $moduleFullPath"
}

$script:AssertionCount = 0

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:AssertionCount++
}

function New-SyntheticItem {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$RootId,
        [Parameter(Mandatory)][string]$Name,
        [string]$State = 'completed',
        [bool]$Registered = $true,
        [bool]$Keep = $false,
        [bool]$SizeKnown = $true,
        [long]$Bytes = 0,
        [long]$CompletionTicks = 0,
        [string[]]$References = @(),
        [string]$BlockedReason = $null
    )

    $workspacePath = [System.IO.Path]::Combine($RootPath, $Name)
    $item = & $script:RetentionModule {
        param([string]$ItemRootPath, [string]$ItemRootId, [string]$WorkspacePath)
        New-GWCatalogItem -RootPath $ItemRootPath -RootId $ItemRootId -WorkspacePath $WorkspacePath
    } $RootPath $RootId $workspacePath

    $item.Registered = $Registered
    $item.State = $State
    $item.Keep = $Keep
    $item.SizeKnown = $SizeKnown
    $item.Bytes = $Bytes
    $item.CompletionTicks = $CompletionTicks
    $item.References = @($References)
    $item.BlockedReason = $BlockedReason
    return $item
}

function New-SyntheticCatalog {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$RootId,
        [Parameter(Mandatory)][object[]]$Items,
        [int]$MaxRecentCompleted = 0,
        [long]$MaxBytes = 0,
        [bool]$AllSizesKnown = $true,
        [long]$TotalBytes = 0,
        [int]$MaxNodes = 5000,
        [int]$MaxMilliseconds = 120000
    )

    return [pscustomobject]@{
        RootPath = $RootPath
        RootId = $RootId
        Items = [object[]]$Items
        Limits = [pscustomobject]@{
            MaxRecentCompleted = $MaxRecentCompleted
            MaxBytes = $MaxBytes
            MaxNodes = $MaxNodes
            MaxMilliseconds = $MaxMilliseconds
        }
        AllSizesKnown = $AllSizesKnown
        TotalBytes = $TotalBytes
    }
}

function Invoke-SyntheticPlan {
    param(
        [Parameter(Mandatory)][object]$TargetCatalog,
        [Parameter(Mandatory)][object[]]$Catalogs
    )

    $catalogArray = [object[]]$Catalogs
    return (& $script:RetentionModule {
        param([object]$PlanTargetCatalog, [object[]]$PlanCatalogs)
        New-GWRetentionPlan -TargetCatalog $PlanTargetCatalog -Catalogs $PlanCatalogs
    } $TargetCatalog $catalogArray)
}

function Test-ProtectedReferenceFanOutNodeBudget {
    $rootPath = 'R:\synthetic-retention-planning\fan-out'
    $items = [System.Collections.Generic.List[object]]::new()
    for ($index = 1; $index -le 64; $index++) {
        $items.Add((New-SyntheticItem -RootPath $rootPath -RootId 'fan-out' -Name ('completed-{0:D2}' -f $index) -Bytes 100 -CompletionTicks $index))
    }

    $references = [string[]]@($items.ToArray() | ForEach-Object { $_.Path })
    $items.Add((New-SyntheticItem -RootPath $rootPath -RootId 'fan-out' -Name 'running-owner' -State 'running' -Bytes 0 -References $references))
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'fan-out' -Items $items.ToArray() -MaxRecentCompleted 0 -MaxBytes 1000 -TotalBytes 6400 -MaxNodes 5000

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    Assert-True -Condition ($plan.Planned.Count -eq 0) -Message 'A running owner must protect every referenced completed candidate.'
    Assert-True -Condition $plan.OverBudget -Message 'Protected candidates must leave the root over its size budget.'
    Assert-True -Condition ($plan.ProjectedBytes -eq 6400) -Message 'Protected candidates must remain in projected size.'
}

function Test-RecentTargetCanBeEvictedForSize {
    $rootPath = 'R:\synthetic-retention-planning\recent-size'
    $older = New-SyntheticItem -RootPath $rootPath -RootId 'recent-size' -Name 'older' -Bytes 100 -CompletionTicks 10
    $recent = New-SyntheticItem -RootPath $rootPath -RootId 'recent-size' -Name 'recent' -Bytes 100 -CompletionTicks 20
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'recent-size' -Items @($older, $recent) -MaxRecentCompleted 1 -MaxBytes 50 -TotalBytes 200

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    $recentPlan = @($plan.Planned | Where-Object { $_.Item.Key -eq $recent.Key }) | Select-Object -First 1
    Assert-True -Condition ($plan.Planned.Count -eq 2) -Message 'The older non-recent item and the recent size candidate must both be planned.'
    Assert-True -Condition ($null -ne $recentPlan) -Message 'A target-root recent item must remain eligible for size-budget eviction.'
    Assert-True -Condition ($recentPlan.Reason -eq 'Root size budget requires evicting this completed workspace.') -Message 'The target recent item must be evicted by the size rule.'
    Assert-True -Condition ($plan.ProjectedBytes -eq 0 -and -not $plan.OverBudget) -Message 'Evicting the target recent item must bring the known size below budget.'
}

function Test-CrossRootCompletedOwnerProtectsTarget {
    $targetRoot = 'R:\synthetic-retention-planning\cross-target'
    $ownerRoot = 'S:\synthetic-retention-planning\cross-owner'
    $target = New-SyntheticItem -RootPath $targetRoot -RootId 'cross-target' -Name 'candidate' -Bytes 100 -CompletionTicks 10
    $owner = New-SyntheticItem -RootPath $ownerRoot -RootId 'cross-owner' -Name 'completed-owner' -Bytes 10 -CompletionTicks 20 -References @($target.Path)
    $targetCatalog = New-SyntheticCatalog -RootPath $targetRoot -RootId 'cross-target' -Items @($target) -MaxRecentCompleted 0 -MaxBytes 0 -TotalBytes 100
    $ownerCatalog = New-SyntheticCatalog -RootPath $ownerRoot -RootId 'cross-owner' -Items @($owner) -MaxRecentCompleted 0 -MaxBytes 100 -TotalBytes 10

    $plan = Invoke-SyntheticPlan -TargetCatalog $targetCatalog -Catalogs @($targetCatalog, $ownerCatalog)
    Assert-True -Condition ($plan.Planned.Count -eq 0) -Message 'A completed owner in another root must protect its target dependency.'
    Assert-True -Condition $plan.OverBudget -Message 'The cross-root protected target must remain over budget.'
    Assert-True -Condition ($plan.InitialClosure.ReferencedKeys.Contains($target.Key)) -Message 'The initial reference closure must include the cross-root dependency.'
}

function Test-KeepAndUnknownLifecycleItemsRemainProtected {
    $rootPath = 'R:\synthetic-retention-planning\keep-unknown'
    $kept = New-SyntheticItem -RootPath $rootPath -RootId 'keep-unknown' -Name 'kept' -Bytes 100 -CompletionTicks 10 -Keep $true
    $unknown = New-SyntheticItem -RootPath $rootPath -RootId 'keep-unknown' -Name 'unknown-legacy' -State 'unknown' -Registered $false -Bytes 100 -CompletionTicks 0
    $recentCandidate = New-SyntheticItem -RootPath $rootPath -RootId 'keep-unknown' -Name 'recent-candidate' -Bytes 100 -CompletionTicks 20
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'keep-unknown' -Items @($kept, $unknown, $recentCandidate) -MaxRecentCompleted 1 -MaxBytes 100 -TotalBytes 300

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    $candidatePlan = @($plan.Planned | Where-Object { $_.Item.Key -eq $recentCandidate.Key }) | Select-Object -First 1
    $retainedPaths = @($plan.Retained | ForEach-Object { [string]$_.path })
    Assert-True -Condition ($plan.Planned.Count -eq 1 -and $null -ne $candidatePlan) -Message 'Only the ordinary completed candidate may be evicted.'
    Assert-True -Condition ($candidatePlan.Reason -eq 'Root size budget requires evicting this completed workspace.') -Message 'The ordinary candidate must be evicted to satisfy size pressure.'
    Assert-True -Condition ($retainedPaths -contains $kept.Path) -Message 'Keep items must remain retained.'
    Assert-True -Condition ($retainedPaths -contains $unknown.Path) -Message 'Unregistered unknown legacy items must remain retained.'
    Assert-True -Condition ($plan.OverBudget -and $plan.ProjectedBytes -eq 200) -Message 'Protected Keep and unknown items must leave the root over budget.'
}

function Test-UnknownSizeSkipsSizeEviction {
    $rootPath = 'R:\synthetic-retention-planning\unknown-size'
    $unknownSize = New-SyntheticItem -RootPath $rootPath -RootId 'unknown-size' -Name 'unknown-size-recent' -SizeKnown $false -Bytes 0 -CompletionTicks 10
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'unknown-size' -Items @($unknownSize) -MaxRecentCompleted 1 -MaxBytes 0 -AllSizesKnown $false -TotalBytes 100

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    Assert-True -Condition (-not $plan.SizeBudgetKnown) -Message 'The plan must preserve the catalog unknown-size state.'
    Assert-True -Condition ($plan.Planned.Count -eq 0) -Message 'Size-based eviction must be skipped when the catalog size is unknown.'
    Assert-True -Condition ($plan.OverBudget -and $plan.OverBudgetReason -like '*size-based eviction was skipped*') -Message 'Unknown aggregate size must be reported as over budget with the skip reason.'
}

function Test-ReferenceChainsAndCyclesStayProtected {
    $rootPath = 'R:\synthetic-retention-planning\chains-cycles'
    $chainHead = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'chain-head' -Bytes 100 -CompletionTicks 10
    $chainTail = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'chain-tail' -Bytes 100 -CompletionTicks 11
    $chainLeaf = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'chain-leaf' -Bytes 100 -CompletionTicks 12
    $cycleA = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'cycle-a' -Bytes 100 -CompletionTicks 13
    $cycleB = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'cycle-b' -Bytes 100 -CompletionTicks 14
    $selfReference = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'self-reference' -Bytes 100 -CompletionTicks 15
    $chainHead.References = @($chainTail.Path)
    $chainTail.References = @($chainLeaf.Path)
    $cycleA.References = @($cycleB.Path)
    $cycleB.References = @($cycleA.Path)
    $selfReference.References = @($selfReference.Path)
    $owner = New-SyntheticItem -RootPath $rootPath -RootId 'chains-cycles' -Name 'running-owner' -State 'running' -Bytes 0 -References @($chainHead.Path, $cycleA.Path, $selfReference.Path)
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'chains-cycles' -Items @($chainHead, $chainTail, $chainLeaf, $cycleA, $cycleB, $selfReference, $owner) -MaxRecentCompleted 0 -MaxBytes 0 -TotalBytes 600

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    Assert-True -Condition ($plan.Planned.Count -eq 0) -Message 'A running owner must preserve dependencies reached through chains and cycles.'
    foreach ($item in @($chainHead, $chainTail, $chainLeaf, $cycleA, $cycleB, $selfReference)) {
        Assert-True -Condition ($plan.InitialClosure.ReferencedKeys.Contains($item.Key)) -Message "The rooted dependency must be marked referenced: $($item.Path)"
    }
    Assert-True -Condition ($plan.OverBudget -and $plan.ProjectedBytes -eq 600) -Message 'The rooted dependency graph must remain above budget without being evicted.'
}

function Test-SelfReferenceDoesNotProtectItsOwnRecentCandidate {
    $rootPath = 'R:\synthetic-retention-planning\self-reference'
    $selfReference = New-SyntheticItem -RootPath $rootPath -RootId 'self-reference' -Name 'self-only' -Bytes 100 -CompletionTicks 10
    $selfReference.References = @($selfReference.Path)
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'self-reference' -Items @($selfReference) -MaxRecentCompleted 1 -MaxBytes 0 -TotalBytes 100

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    Assert-True -Condition ($plan.Planned.Count -eq 1) -Message 'A self-reference must not make a recent candidate its own external protector.'
    Assert-True -Condition ($plan.Planned[0].Item.Key -eq $selfReference.Key) -Message 'The self-referencing candidate must be the planned item.'
    Assert-True -Condition ($plan.Planned[0].Reason -eq 'Root size budget requires evicting this completed workspace.') -Message 'The self-referencing candidate must be selected by size pressure.'
    Assert-True -Condition ($plan.ProjectedBytes -eq 0 -and -not $plan.OverBudget) -Message 'Evicting the self-referencing candidate must satisfy the budget.'
}

function Test-OrdinarySizeEvictionOrder {
    $rootPath = 'R:\synthetic-retention-planning\ordinary-eviction'
    $older = New-SyntheticItem -RootPath $rootPath -RootId 'ordinary-eviction' -Name 'older' -Bytes 100 -CompletionTicks 10
    $newer = New-SyntheticItem -RootPath $rootPath -RootId 'ordinary-eviction' -Name 'newer' -Bytes 100 -CompletionTicks 20
    $catalog = New-SyntheticCatalog -RootPath $rootPath -RootId 'ordinary-eviction' -Items @($older, $newer) -MaxRecentCompleted 2 -MaxBytes 100 -TotalBytes 200

    $plan = Invoke-SyntheticPlan -TargetCatalog $catalog -Catalogs @($catalog)
    Assert-True -Condition ($plan.Planned.Count -eq 1) -Message 'Only one ordinary completed item should be evicted to meet the budget.'
    Assert-True -Condition ($plan.Planned[0].Item.Key -eq $older.Key) -Message 'The oldest ordinary completed item must be selected first.'
    Assert-True -Condition ($plan.Planned[0].Reason -eq 'Root size budget requires evicting this completed workspace.') -Message 'The ordinary eviction must be attributed to the size budget.'
    Assert-True -Condition ($plan.ProjectedBytes -eq 100 -and -not $plan.OverBudget) -Message 'The ordinary eviction must reduce projected size to the configured limit.'
}

Test-ProtectedReferenceFanOutNodeBudget
Test-RecentTargetCanBeEvictedForSize
Test-CrossRootCompletedOwnerProtectsTarget
Test-KeepAndUnknownLifecycleItemsRemainProtected
Test-UnknownSizeSkipsSizeEviction
Test-ReferenceChainsAndCyclesStayProtected
Test-SelfReferenceDoesNotProtectItsOwnRecentCandidate
Test-OrdinarySizeEvictionOrder

Write-Output "GeneratedWorkspaceRetentionPlanning.Tests passed ($script:AssertionCount assertions)."

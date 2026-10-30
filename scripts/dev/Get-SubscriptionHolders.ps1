<#
.SYNOPSIS
Lists every current subscription holder from GET /api/debug/subscriptions as one compact table.

.DESCRIPTION
One row per holder: source, holder, interval, item count, and flags (explicit interval, persistent,
expired-but-not-yet-removed). Sampling roles are named by the StateItemUids fact that
SubscriptionActivityFacts binds to each role, read from this checkout. -Tray compares the holders with
src/Development/ControlDecisionAcceptance/TrayExpectation.cs: holders on a role whose in-use fact the tray
expectation requires false are listed as unexpected. TrayExpectation lists facts, not holders, so holders on
roles it requires true, Present holders and kernel ETW holders are counted but not judged.

The endpoint is a live debug read: no lease, no sampling, and it may be stale by the time it prints.

.EXAMPLE
pwsh scripts/dev/Get-SubscriptionHolders.ps1
pwsh scripts/dev/Get-SubscriptionHolders.ps1 -Tray
pwsh scripts/dev/Get-SubscriptionHolders.ps1 -Port 9330 -AccessKey $key -ShowItems
#>
param(
    [int]$Port = 9321,
    [string]$AccessKey,
    [switch]$Tray,
    [switch]$ShowItems
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$factsPath = Join-Path $repo 'src/Core/Infrastructure/Monitoring/SubscriptionActivityFacts.cs'
$trayPath = Join-Path $repo 'src/Development/ControlDecisionAcceptance/TrayExpectation.cs'

# Role -> StateItemUids fact name, from the existing BindRole(n, StateItemUids.X) lines.
$roleFacts = @{}
if (Test-Path -LiteralPath $factsPath) {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $factsPath -Raw), 'BindRole\((\d+),\s*StateItemUids\.(\w+)\)')) {
        $roleFacts[[int]$match.Groups[1].Value] = $match.Groups[2].Value
    }
}

$request = @{ Uri = "http://127.0.0.1:$Port/api/debug/subscriptions"; TimeoutSec = 15 }
if ($AccessKey) { $request.Headers = @{ 'X-Resource-Manager-Key' = $AccessKey } }
else {
    $request.UseDefaultCredentials = $true
    if ($PSVersionTable.PSVersion.Major -ge 7) { $request.AllowUnencryptedAuthentication = $true }
}
$payload = Invoke-RestMethod @request

function Format-Name([string]$text, [int]$width) {
    if ($text.Length -le $width) { return $text }
    return $text.Substring(0, $width - 3) + '...'
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($role in $payload.sampling) {
    $fact = $roleFacts[[int]$role.roleId]
    $source = if ($fact) { "$($role.roleId):$($fact -replace '^Sampling|InUse$', '')" } else { "$($role.roleId):$($role.requestType)" }
    foreach ($holder in $role.holders) {
        $flags = @()
        if ($holder.explicitInterval) { $flags += 'explicit' } else { $flags += 'default' }
        if ($holder.persistent) { $flags += 'persistent' }
        if (-not $holder.inUse) { $flags += 'expired' }
        $rows.Add([pscustomobject]@{
            Kind = 'sampling'; Role = [int]$role.roleId; Fact = $fact; Source = $source; Holder = [string]$holder.holder
            Interval = if ($null -ne $holder.intervalMilliseconds) { "$($holder.intervalMilliseconds)ms" } else { '-' }
            Items = @($holder.itemIds).Count; ItemIds = @($holder.itemIds); Flags = $flags -join ','
        })
    }
}
foreach ($holder in $payload.present) {
    $rows.Add([pscustomobject]@{
        Kind = 'present'; Role = $null; Fact = $null; Source = "present:$($holder.kind)"; Holder = [string]$holder.id
        Interval = 'events'; Items = @($holder.executablePath | Where-Object { $_ }).Count
        ItemIds = @($holder.executablePath | Where-Object { $_ }); Flags = if ($holder.needsEvents) { 'needs-events' } else { 'idle' }
    })
}
foreach ($holder in $payload.kernel.holders) {
    $rows.Add([pscustomobject]@{
        Kind = 'kernel'; Role = $null; Fact = $null; Source = 'kernel-etw'; Holder = [string]$holder.id
        Interval = 'events'; Items = 1; ItemIds = @($holder.keywords); Flags = $holder.keywords
    })
}

$roleCount = @($payload.sampling).Count
"sampling roles bound: $roleCount; holders: sampling=$(@($rows | Where-Object Kind -eq 'sampling').Count) present=$(@($payload.present).Count) kernel=$(@($payload.kernel.holders).Count) (kernel session $($payload.kernel.state), keywords $($payload.kernel.enabledKeywords))"
foreach ($role in $payload.sampling) {
    if (@($role.holders).Count -eq 0) { "  role $($role.roleId) ($($role.requestType)): no holders, default $(if ($null -ne $role.defaultIntervalMilliseconds) { "$($role.defaultIntervalMilliseconds)ms" } else { 'unpublished' })" }
}
$rows | ForEach-Object {
    $line = '{0,-26} {1,-52} {2,8} {3,5}  {4}' -f (Format-Name $_.Source 26), (Format-Name $_.Holder 52), $_.Interval, $_.Items, $_.Flags
    $line
    if ($ShowItems -and $_.Items -gt 0) { '    items: ' + (Format-Name ((@($_.ItemIds) | Select-Object -First 12) -join ', ') 160) }
}

if ($Tray) {
    if (-not (Test-Path -LiteralPath $trayPath)) { throw "TrayExpectation not found: $trayPath" }
    $trayText = Get-Content -LiteralPath $trayPath -Raw
    function Read-FactList([string]$name) {
        $block = [regex]::Match($trayText, "$name\s*=\s*\[(?<body>[^\]]*)\]")
        if (-not $block.Success) { throw "TrayExpectation.$name not found" }
        return @([regex]::Matches($block.Groups['body'].Value, 'StateItemUids\.(\w+)') | ForEach-Object { $_.Groups[1].Value })
    }
    $requiredFalse = Read-FactList 'RequiredFalseFacts'
    $requiredTrue = Read-FactList 'RequiredTrueFacts'
    $unexpected = @($rows | Where-Object { $_.Kind -eq 'sampling' -and $_.Fact -and $requiredFalse -contains $_.Fact -and $_.Flags -notmatch 'expired' })
    $unmapped = @($rows | Where-Object { $_.Kind -eq 'sampling' -and -not $_.Fact })
    $expectedRoles = @($rows | Where-Object { $_.Kind -eq 'sampling' -and $requiredTrue -contains $_.Fact })
    ''
    "tray expectation (TrayExpectation.cs): unexpected=$($unexpected.Count) unmapped=$($unmapped.Count) on-expected-roles=$($expectedRoles.Count) not-judged(present+kernel)=$(@($rows | Where-Object Kind -ne 'sampling').Count)"
    foreach ($row in $unexpected) { "  UNEXPECTED $($row.Source) $($row.Holder) $($row.Interval) items=$($row.Items) (TrayExpectation requires $($row.Fact)=false)" }
    foreach ($row in $unmapped) { "  UNMAPPED   $($row.Source) $($row.Holder) (role has no StateItemUids fact in SubscriptionActivityFacts)" }
}

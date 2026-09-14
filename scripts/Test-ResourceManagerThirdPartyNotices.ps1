[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackendPublishRoot,
    [Parameter(Mandatory = $true)][string]$NativeUiPublishRoot,
    [Parameter(Mandatory = $true)][string]$LauncherPublishRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'Resource Manager\Resource Manager-APP'
$backend = [IO.Path]::GetFullPath($BackendPublishRoot)
$ui = [IO.Path]::GetFullPath($NativeUiPublishRoot)
$launcher = [IO.Path]::GetFullPath($LauncherPublishRoot)
$noticeRoot = Join-Path $app 'ThirdPartyNotices'
$inventory = [IO.File]::ReadAllText((Join-Path $noticeRoot 'README.md'))
$managedInventory = [IO.File]::ReadAllText((Join-Path $noticeRoot 'ManagedDependencyAcknowledgements.md'))
$checked = [Collections.Generic.List[object]]::new()

function Assert-CopiedNotice([string]$Source, [string]$Destination) {
    if (-not [IO.File]::Exists($Destination)) { throw "Published notice missing: $Destination" }
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ($sourceHash -cne $publishedHash) { throw "Published notice changed: $Destination" }
    $checked.Add([pscustomobject]@{Source=$Source;Published=$Destination;Sha256=$publishedHash})
}

foreach ($file in (Get-ChildItem -LiteralPath $noticeRoot -File)) {
    Assert-CopiedNotice $file.FullName (Join-Path $backend ('ThirdPartyNotices\' + $file.Name))
    Assert-CopiedNotice $file.FullName (Join-Path $launcher ('ThirdPartyNotices\' + $file.Name))
}
Assert-CopiedNotice (Join-Path $repo 'LICENSE') (Join-Path $backend 'LICENSE')
Assert-CopiedNotice (Join-Path $repo 'LICENSE') (Join-Path $ui 'LICENSE')
Assert-CopiedNotice (Join-Path $repo 'LICENSE') (Join-Path $launcher 'LICENSE')
foreach ($root in @($backend, $ui, $launcher)) {
    Assert-CopiedNotice (Join-Path $repo 'NOTICE') (Join-Path $root 'NOTICE')
}
foreach ($name in @('WebView2.LICENSE.txt', 'WebView2.NOTICE.txt', 'DotNet.LICENSE.txt',
        'DotNet.THIRD-PARTY-NOTICES.txt', 'Microsoft.MIT.txt', 'ManagedDependencyAcknowledgements.md')) {
    Assert-CopiedNotice (Join-Path $noticeRoot $name) (Join-Path $ui ('ThirdPartyNotices\' + $name))
}
Assert-CopiedNotice (Join-Path $app 'Native\GpuPlacementShim\third_party\minhook\LICENSE.txt') `
    (Join-Path $backend 'ThirdPartyNotices\MinHook.LICENSE.txt')
Assert-CopiedNotice (Join-Path $app 'Native\GpuPlacementShim\third_party\detours\LICENSE.md') `
    (Join-Path $backend 'ThirdPartyNotices\MicrosoftDetours.LICENSE.md')
Assert-CopiedNotice (Join-Path $app 'Infrastructure\Resources\DeviceIds\THIRD_PARTY_NOTICES.md') `
    (Join-Path $backend 'DeviceIds\THIRD_PARTY_NOTICES.md')
Assert-CopiedNotice (Join-Path $app 'Infrastructure\Resources\SoftwareMetadata\THIRD_PARTY_NOTICES.md') `
    (Join-Path $backend 'Infrastructure\Resources\SoftwareMetadata\THIRD_PARTY_NOTICES.md')

$packages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($assetsPath in @('obj\project.assets.json', 'NativeUi\obj\project.assets.json')) {
    $assets = Get-Content -LiteralPath (Join-Path $app $assetsPath) -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $name, $version = $library.Name.Split('/')
        if (-not $inventory.Contains('| ' + $name + ' | ' + $version + ' |') -and
            -not $managedInventory.Contains('| ' + $name + '/' + $version + ' |')) {
            throw "Resolved package is missing or has a stale notice version: $($library.Name)"
        }
        [void]$packages.Add($library.Name)
    }
}
[pscustomobject]@{
    Passed = $true
    Purpose = 'Notice copies and resolved package versions; not a complete license or release approval'
    CheckedFiles = $checked.Count
    CheckedPackages = $packages.Count
    Files = $checked
} | ConvertTo-Json -Depth 5

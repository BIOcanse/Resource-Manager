[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DeclarationPath,
    [Parameter(Mandatory)][string]$NativeRoot,
    [Parameter(Mandatory)][string]$DependencyManifestPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Read-Contract([string]$Path) {
    if (-not [IO.File]::Exists($Path) -or (Get-Item -LiteralPath $Path).Length -gt 1048576) {
        throw "Missing or oversized Disk build contract: $Path. Generate the RM Native through ProductDiskNative first."
    }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
$declaration = Read-Contract $DeclarationPath
$dependency = Read-Contract $DependencyManifestPath
$manifest = Read-Contract (Join-Path $NativeRoot 'native.json')
if ($declaration.schema -cne 'disk.dotnet.declaration.v1' -or
    $dependency.schema -cne 'rm/disk-sdk-dependency/v1' -or
    $manifest.schema -cne 'disk.dotnet.native.v1' -or
    $manifest.sdk_version -cne $dependency.nativeSdkVersion -or
    $manifest.builder_version -cne $dependency.builderVersion -or
    $manifest.sdk_manifest_sha256 -cne $dependency.sdkManifestSha256 -or
    (Get-FileHash -LiteralPath $DeclarationPath).Hash -cne $dependency.declarationSha256) {
    throw 'Disk Native or dependency identity does not match the current RM declaration.'
}
if ($manifest.compile_policy.jobs -ne 1 -or $manifest.compile_policy.backend -cne 'llvm' -or
    $manifest.compile_policy.optimization -cne 'ReleaseSafe' -or
    $declaration.configuration.source -cne 'sdk-default' -or
    $manifest.configuration.source -cne $declaration.configuration.source) {
    throw 'Disk Native build/configuration policy differs from the qualified RM contract.'
}
foreach ($name in @('templates', 'fixed_uids_hex')) {
    if ([string]::Join("`n", [string[]]$declaration.$name) -cne
        [string]::Join("`n", [string[]]$manifest.$name)) {
        throw "Disk Native $name do not match the RM declaration."
    }
}
foreach ($name in @('maximum_readers', 'maximum_payload_bytes', 'maximum_path_bytes',
    'maximum_parameter_bytes', 'maximum_runtime_templates', 'maximum_subscriptions', 'maximum_callback_calls')) {
    if ($declaration.capacity.$name -ne $manifest.capacity.$name) { throw "Disk Native capacity differs: $name" }
}
foreach ($name in @('maximum_directory_entries', 'maximum_uid_bytes', 'maximum_segments_per_uid')) {
    if ($declaration.capacity.catalog.$name -ne $manifest.capacity.catalog.$name) { throw "Disk Native catalog differs: $name" }
}
if ($manifest.library -cne 'Disk.ReadWrite.Native.dll' -or
    (Get-FileHash -LiteralPath (Join-Path $NativeRoot $manifest.library)).Hash -cne $manifest.library_sha256) {
    throw 'Disk Native library bytes do not match the generated identity.'
}
$package = Join-Path (Split-Path -Parent $DependencyManifestPath) $dependency.managedPackage.path
if ((Get-Item -LiteralPath $package).Length -ne $dependency.managedPackage.length -or
    (Get-FileHash -LiteralPath $package).Hash -cne $dependency.managedPackage.sha256) {
    throw 'Formal Disk.ReadWrite package changed.'
}
Write-Output 'Qualified RM Disk checkpoint declaration, Native identity, policy, and managed package match.'

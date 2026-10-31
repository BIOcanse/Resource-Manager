[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '../../src/UI/Desktop/src/generated/product_layout.zig'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ResourceManager.DesktopLayout.ps1')
$layout = Get-ResourceManagerDesktopLayout
$lines = @('// Generated from ProductDesktopLayout.cs by Generate-DesktopLayout.ps1.')
foreach ($property in $layout.PSObject.Properties) {
    $lines += 'pub const ' + $property.Name + ' = "' + $property.Value + '";'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Output))) | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

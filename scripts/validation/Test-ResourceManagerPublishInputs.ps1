[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$app = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\Core'
$raw = & dotnet msbuild (Join-Path $app 'ResourceManager.App.csproj') -nologo -getItem:Content,None,EmbeddedResource
if ($LASTEXITCODE -ne 0) { throw 'Cannot evaluate publication inputs.' }
$items = ($raw -join "`n" | ConvertFrom-Json).Items
foreach ($item in @($items.Content) + @($items.None) + @($items.EmbeddedResource)) {
    if ($item.Identity -match '^(Config|UserData|Dependencies|Misc)[\\/]') {
        throw "Runtime data was admitted to project inputs: $($item.Identity)"
    }
}
foreach ($required in @('Configuration\Cpu\baseline-defaults.json', 'Configuration\FreedomPoints\backend.json',
        'Configuration\HostManager\default.json', 'Configuration\SoftwareProfiles\official.json')) {
    if (-not @($items.EmbeddedResource | Where-Object { $_.Identity -eq $required }).Count) {
        throw "Universal default is missing: $required"
    }
}
'Publication inputs passed: runtime data excluded, universal defaults embedded.'

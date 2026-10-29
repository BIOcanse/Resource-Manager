[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [ValidateSet('true', 'false')][string]$ExpectedUseNls = 'true'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checks = 0
$entries = @(@('service', 'ResourceManager'), @('ui', 'ResourceManager.NativeUi'))
$index = 0
while ($index -lt $entries.Count) {
    $entry = $entries[$index]
    $index++
    $path = Join-Path $PublishRoot ($entry[0] + '/' + $entry[1] + '.runtimeconfig.json')
    $configuration = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $properties = $configuration.runtimeOptions.configProperties
    if ($properties.'System.Globalization.UseNls' -isnot [bool] -or
        $properties.'System.Globalization.UseNls' -ne ($ExpectedUseNls -eq 'true')) {
        throw "Published $path does not carry System.Globalization.UseNls=$ExpectedUseNls as a JSON boolean."
    }
    if ($properties.'System.Globalization.Invariant' -isnot [bool] -or $properties.'System.Globalization.Invariant') {
        throw "Published $path must preserve named cultures (System.Globalization.Invariant=false)."
    }
    $checks += 2
}
"Passed: $checks published globalization settings ($PublishRoot)"

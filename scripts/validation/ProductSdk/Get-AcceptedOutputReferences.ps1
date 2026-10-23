[CmdletBinding()]
param([Parameter(Mandatory)][object]$Acceptance)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A build location records past execution. Only declared consumers keep it alive.
$references = @($Acceptance.packages | ForEach-Object path) +
    @($Acceptance.consumerDependencies | ForEach-Object deps)
if ($Acceptance.PSObject.Properties['retainedOutputs']) {
    $references += @($Acceptance.retainedOutputs)
}
$references | Sort-Object -Unique

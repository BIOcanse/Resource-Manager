#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $TestRoot) { throw 'The script contract test root must be new.' }
[void][IO.Directory]::CreateDirectory($TestRoot)

function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Read-ScriptAst([string]$RelativePath) {
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repository $RelativePath), [ref]$null, [ref]$errors)
    Assert-Contract ($errors.Count -eq 0) "Parse failed: $RelativePath"
    return $ast
}

# Execute the production key loading, empty-file guard and header construction.
# No HTTP request is sent and no installed product file is read.
$keyFile = Join-Path $TestRoot 'fixture-key'
foreach ($case in @(
        @{ Script = 'Record-DecisionStream.ps1'; Variable = 'token'; Header = 'TryAddWithoutValidation' },
        @{ Script = 'Invoke-Cs2BenchmarkSeries.ps1'; Variable = 'productKey'; Header = '$headers = @{' })) {
    $ast = Read-ScriptAst ('tests\Validation\NonAdaptedOptimizationLab\GameBenchmarks\' + $case.Script)
    $variable = [regex]::Escape('$' + $case.Variable)
    $keyLoad = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Extent.Text -match ('^' + $variable + '\s*=')
    }, $true))
    $guard = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text -match ('^if \(\[string\]::IsNullOrWhiteSpace\(' + $variable + '\)')
    }, $true))
    $header = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.StatementAst] -and
        $node.Extent.Text.Contains('X-Resource-Manager-Key') -and
        $node.Extent.Text.StartsWith($case.Header)
    }, $true))
    # The recorder's statement starts with its void cast.
    if ($case.Variable -eq 'token') {
        $header = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.PipelineAst] -and
            $node.Extent.Text.StartsWith('[void]$request.Headers.TryAddWithoutValidation')
        }, $true))
    }
    Assert-Contract ($keyLoad.Count -eq 1 -and $guard.Count -eq 1 -and $header.Count -eq 1) `
        "Expected one key load, empty-file guard and header in $($case.Script)."
    $TokenFile = $keyFile
    $ProductTokenFile = $keyFile
    [IO.File]::WriteAllText($keyFile, "  synthetic-revocable-key`n")
    . ([scriptblock]::Create($keyLoad[0].Extent.Text))
    . ([scriptblock]::Create($guard[0].Extent.Text))
    $request = [Net.Http.HttpRequestMessage]::new()
    try {
        . ([scriptblock]::Create($header[0].Extent.Text))
        $actual = if ($case.Variable -eq 'token') {
            @($request.Headers.GetValues('X-Resource-Manager-Key'))[0]
        } else { $headers['X-Resource-Manager-Key'] }
        Assert-Contract ($actual -ceq 'synthetic-revocable-key') 'The supplied key was not attached.'
        if ($case.Variable -eq 'productKey') {
            Assert-Contract ($headers.Count -eq 1) 'The preflight attached an unexpected authorization header.'
        }
        Assert-Contract (-not $request.Headers.Contains('X-Resource-Manager-Token')) 'Removed header was attached.'
    } finally { $request.Dispose() }
    [IO.File]::WriteAllText($keyFile, " `n")
    . ([scriptblock]::Create($keyLoad[0].Extent.Text))
    $rejected = $false
    try { . ([scriptblock]::Create($guard[0].Extent.Text)) } catch { $rejected = $true }
    Assert-Contract $rejected "Empty key permitted keyless recording in $($case.Script)."
}

. (Join-Path $repository 'scripts\ResourceManager.MachineDeployment.ps1')

# A fresh installer that promoted nothing never owns shared registrations,
# even if a caller already marked machine mutation as started.
Assert-Contract ((Get-ResourceManagerMachineInstallRollbackMode `
    -MachineMutationStarted $true -HadExistingInstallation $false `
    -HadPreviousBackup $false -PromotedNewInstallation $false) -ceq 'NoMachineMutation') `
    'A fresh install without promotion selected destructive machine rollback.'

# Verify the existing checked command runner's fail-closed exit-code contract.
function Invoke-FixtureAdministration { $global:LASTEXITCODE = 13 }
$rejected = $false
try {
    Invoke-ResourceManagerNativeCommand -FilePath 'Invoke-FixtureAdministration' `
        -Arguments @('--initialize-trusted-access')
} catch { $rejected = $_.Exception.Message.Contains('exit code 13') }
Assert-Contract $rejected 'Nonzero initialization exit code was accepted.'

# Run the actual installation orchestrator with all machine actions mocked.
# Only fixture files beneath TestRoot are created or promoted.
function Assert-ResourceManagerDeploymentPlan { }
function Test-ResourceManagerAdministrator { return $true }
function Assert-ResourceManagerMachineInstallTargetOwnership { }
function Copy-ResourceManagerDirectoryTree { param($Source, $Destination) [void][IO.Directory]::CreateDirectory($Destination) }
function Write-ResourceManagerInstallationMarker { }
function Assert-ResourceManagerInstallationMarker { }
function Set-ResourceManagerInstallAcl { }
function Stop-ResourceManagerMachineService { $script:calls.Add('stop') }
function Set-ResourceManagerMachineServiceRegistration { $script:calls.Add('register') }
function Start-ResourceManagerMachineService { $script:calls.Add('start') }
function Set-ResourceManagerMachineRegistry { $script:calls.Add('registry') }
function Remove-ResourceManagerLegacyStartup { $script:calls.Add('legacy') }
function Remove-ResourceManagerMachineServiceRegistration { $script:calls.Add('remove-service') }
function Remove-ResourceManagerOwnedDirectory { }
$script:registryCleanupCalls = [Collections.Generic.List[string]]::new()
function Remove-Item {
    param($LiteralPath, [switch]$Recurse, [switch]$Force, $ErrorAction)
    Assert-Contract ($LiteralPath.StartsWith('FixtureRegistry:')) 'Unexpected cleanup mutation in mock test.'
    $script:registryCleanupCalls.Add($LiteralPath)
}
function Invoke-ResourceManagerNativeCommand {
    param($FilePath, $Arguments)
    Assert-Contract ($FilePath -ceq $script:expectedBackend) 'Initialization used the wrong executable.'
    Assert-Contract ($Arguments.Count -eq 1 -and $Arguments[0] -ceq '--initialize-trusted-access') `
        'Installation used an unexpected administration action.'
    $script:calls.Add('initialize')
    if ($script:failInitialization) { throw 'Synthetic initialization failure.' }
}
$validator = Join-Path $TestRoot 'validator.ps1'
[IO.File]::WriteAllText($validator, 'param($RootDirectory, $ExecutablePath, $ExpectedExecutionLevel)')
foreach ($failure in @($false, $true)) {
    $script:failInitialization = $failure
    $script:calls = [Collections.Generic.List[string]]::new()
    $install = Join-Path $TestRoot "install-$failure"
    $script:expectedBackend = Join-Path $install 'Current\ResourceManager\ResourceManager.exe'
    $plan = [pscustomobject]@{
        InstallRoot = $install; SourceImageRoot = $TestRoot; BackendPath = $script:expectedBackend; NativeUiPath = $validator
        ServiceName = 'Fixture.Service'; InstallerScriptPath = $validator; DeploymentModulePath = $validator
        FinalImageValidatorPath = $validator; ExecutableManifestValidatorPath = $validator; SourceNativeUiPath = $validator
        MachineAppPathsKey = 'FixtureRegistry:AppPaths'; MachineUninstallKey = 'FixtureRegistry:Uninstall'
        MachineRegistryRoot = 'FixtureRegistry:Product'
    }
    $rejected = $false
    try { $null = Invoke-ResourceManagerMachineInstallCore -Plan $plan } catch {
        if (-not $failure -or $_.Exception.Message -cne 'Synthetic initialization failure.') { throw }
        $rejected = $true
    }
    if ($failure) {
        Assert-Contract $rejected 'Failed initialization did not reject installation.'
        Assert-Contract (($script:calls -join ',') -ceq 'stop,initialize,stop,remove-service') `
            'Failed initialization reached service registration/start or skipped fresh-install rollback.'
    } else {
        Assert-Contract (($script:calls -join ',') -ceq 'stop,initialize,register,start,registry,legacy') `
            'Initialization did not precede registration and start.'
    }
}

# Existing-install rejection preserves both an enabled and a deliberately disabled
# default, all persisted key records, and the legacy AI migration input verbatim.
foreach ($disabled in @($false, $true)) {
    $install = Join-Path $TestRoot "existing-$disabled"
    $security = Join-Path $install 'UserData\Security'
    $legacy = Join-Path $install 'Current\ResourceManager\UserData\resource-manager.db'
    [void][IO.Directory]::CreateDirectory($security)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($legacy))
    $default = Join-Path $security 'default-key'
    $store = Join-Path $security 'keys-fixture.db'
    [IO.File]::WriteAllText($default, $(if ($disabled) { '' } else { 'synthetic-existing-key' }))
    [IO.File]::WriteAllText($store, 'synthetic-existing-key-hashes-and-disabled-state')
    [IO.File]::WriteAllText($legacy, 'synthetic-legacy-ai-migration-input')
    $before = @{}
    foreach ($path in @($default, $store, $legacy)) { $before[$path] = [IO.File]::ReadAllBytes($path) }
    $script:calls.Clear()
    $rejected = $false
    try { $null = Invoke-ResourceManagerMachineInstallCore -Plan ([pscustomobject]@{ InstallRoot = $install }) }
    catch {
        $rejected = $_.Exception -is [InvalidOperationException] -and
            $_.Exception.Message.StartsWith('MachineDeploymentUpgradeNotSupported:') -and
            $_.Exception.Message.Contains('native package installer')
    }
    Assert-Contract $rejected 'Existing installation did not receive an actionable typed rejection.'
    Assert-Contract ($script:calls.Count -eq 0) 'Existing installation triggered service or key operations.'
    foreach ($path in @($default, $store, $legacy)) {
        Assert-Contract ([Convert]::ToBase64String($before[$path]) -ceq
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) 'Existing credential/data bytes changed.'
    }
    Assert-Contract (@([IO.Directory]::GetDirectories($TestRoot, "existing-$disabled.staging.*")).Count -eq 0) `
        'Existing installation created a staging tree before rejection.'
}

# A destination arriving while the image is staged must also fail before stop.
$raceInstall = Join-Path $TestRoot 'arrived-during-staging'
function Set-ResourceManagerInstallAcl {
    [void][IO.Directory]::CreateDirectory($raceInstall)
    [IO.File]::WriteAllText((Join-Path $raceInstall 'existing-marker'), 'existing-data')
}
$plan.InstallRoot = $raceInstall
$script:calls.Clear()
$rejected = $false
try { $null = Invoke-ResourceManagerMachineInstallCore -Plan $plan } catch {
    $rejected = $_.Exception -is [InvalidOperationException] -and
        $_.Exception.Message.StartsWith('MachineDeploymentUpgradeNotSupported:')
}
Assert-Contract $rejected 'A destination arriving during staging was replaced.'
Assert-Contract ($script:calls.Count -eq 0) 'A destination arriving during staging triggered service/key operations.'
Assert-Contract ([IO.File]::ReadAllText((Join-Path $raceInstall 'existing-marker')) -ceq 'existing-data') `
    'The destination arriving during staging was changed.'

# Reproduce the narrower race: the real final vacancy check passes, then a
# competitor creates its root and synthetic service/registry registration before
# the production Directory.Move. No real service or registry operation is used.
function Set-ResourceManagerInstallAcl { }
$script:freshTargetCheck = ${function:Assert-ResourceManagerFreshInstallTarget}
$script:freshTargetChecks = 0
$script:competitorRegistration = @{}
function Assert-ResourceManagerFreshInstallTarget {
    param([string]$InstallRoot)
    & $script:freshTargetCheck -InstallRoot $InstallRoot
    $script:freshTargetChecks++
    if ($script:freshTargetChecks -eq 2) {
        [void][IO.Directory]::CreateDirectory($InstallRoot)
        [IO.File]::WriteAllText((Join-Path $InstallRoot 'competitor-marker'), 'competitor-data')
        $script:competitorRegistration.Service = 'competitor-service'
        $script:competitorRegistration.Registry = 'competitor-product-registration'
    }
}
$raceInstall = Join-Path $TestRoot 'arrived-after-final-vacancy-check'
$plan.InstallRoot = $raceInstall
$script:calls.Clear()
$script:registryCleanupCalls.Clear()
$rejected = $false
try { $null = Invoke-ResourceManagerMachineInstallCore -Plan $plan } catch {
    $rejected = $_.Exception.InnerException -is [IO.IOException]
}
Assert-Contract ($script:freshTargetChecks -eq 2) 'The race did not reach the final successful vacancy check.'
Assert-Contract $rejected 'Directory.Move did not reject the competing installation.'
Assert-Contract ($script:calls.Count -eq 0) 'Failed promotion stopped or removed the competing service.'
Assert-Contract ($script:registryCleanupCalls.Count -eq 0) 'Failed promotion removed competing registry registration.'
Assert-Contract ($script:competitorRegistration.Service -ceq 'competitor-service' -and
    $script:competitorRegistration.Registry -ceq 'competitor-product-registration') `
    'Failed promotion changed the competing registration.'
Assert-Contract ([IO.File]::ReadAllText((Join-Path $raceInstall 'competitor-marker')) -ceq 'competitor-data') `
    'Failed promotion changed the competing installation files.'
Write-Output 'PASS: authenticated headers, empty-key rejection, initializer failure/order, existing-install rejection preserving enabled/disabled keys and legacy AI input, and post-vacancy promotion race with zero competing-service/registry cleanup.'

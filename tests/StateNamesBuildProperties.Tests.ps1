[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$marker = Join-Path $WorkRoot '.generated-workspace.json'
if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).state -ne 'running') {
    throw 'WorkRoot must be a running task registered through Manage-GeneratedWorkspace.ps1 New.'
}
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/Core/ResourceManager.App.csproj'
$dotnet = (Get-Command dotnet -CommandType Application).Source
$scratch = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ('state-names-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($scratch)
$script:count = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:count++
}
function Invoke-PropertyEvaluation([string]$Name, [string]$Value) {
    $arguments = @('msbuild', $project, '-nologo', '-target:ValidateResourceManagerStateNames',
        '-getProperty:ResourceManagerStateNames,NativeCoreStateNames,NativeCoreStateNamesArgument,NativeCoreInstallRoot,NativeCoreOutput,DefineConstants')
    if ($Name -ne 'default') { $arguments += "-property:ResourceManagerStateNames=$Value" }
    $lines = @(& $dotnet @arguments 2>&1)
    $code = $LASTEXITCODE
    $output = $lines -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $scratch ($Name + '.log')), $output, [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ ExitCode = $code; Output = $output }
}

$paths = @{}
foreach ($case in @(
    @{ Name = 'default'; Value = ''; Expected = 'true' },
    @{ Name = 'true-lower'; Value = 'true'; Expected = 'true' },
    @{ Name = 'true-title'; Value = 'True'; Expected = 'true' },
    @{ Name = 'false-lower'; Value = 'false'; Expected = 'false' },
    @{ Name = 'false-title'; Value = 'False'; Expected = 'false' }
)) {
    $result = Invoke-PropertyEvaluation $case.Name $case.Value
    Assert-True ($result.ExitCode -eq 0) "$($case.Name): evaluation succeeds. $($result.Output)"
    $properties = ($result.Output | ConvertFrom-Json).Properties
    Assert-True ($properties.NativeCoreStateNames -ceq $case.Expected) "$($case.Name): canonical boolean."
    Assert-True ($properties.NativeCoreStateNamesArgument -ceq ('-Dstate-names=' + $case.Expected)) "$($case.Name): canonical Zig argument."
    Assert-True ($properties.NativeCoreInstallRoot.EndsWith(('\state-names-' + $case.Expected), [StringComparison]::Ordinal)) "$($case.Name): canonical artifact directory."
    Assert-True ($properties.NativeCoreOutput -ceq ($properties.NativeCoreInstallRoot + '\bin\ResourceManager.NativeCore.dll')) "$($case.Name): DLL comes from the same artifact directory."
    Assert-True (($properties.DefineConstants.Split(';') -contains 'RM_STATE_NAMES') -eq ($case.Expected -ceq 'true')) "$($case.Name): managed names follow the same boolean."
    if ($paths.ContainsKey($case.Expected)) {
        Assert-True ($properties.NativeCoreInstallRoot -ceq $paths[$case.Expected]) "$($case.Name): equal booleans share the same artifact."
    } else { $paths[$case.Expected] = $properties.NativeCoreInstallRoot }
}
$invalid = Invoke-PropertyEvaluation 'invalid' 'enabled'
Assert-True ($invalid.ExitCode -ne 0) 'Invalid boolean fails before native compilation.'
Assert-True ($invalid.Output.Contains("ResourceManagerStateNames must be true or false (case-insensitive); received 'enabled'.")) 'Invalid boolean has a clear diagnostic.'
# Bind the evaluated argument to the actual native build, without invoking Zig.
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$nativeBuild = @($projectXml.Project.Target | Where-Object Name -eq 'BuildNativeCore')[0]
Assert-True ($nativeBuild.Exec.Command.Contains('$(NativeCoreStateNamesArgument)')) 'Native build consumes the evaluated argument.'
Assert-True ($nativeBuild.Exec.Command.Contains('$(NativeCoreInstallRoot)')) 'Native build installs to the evaluated artifact directory.'
Write-Output "Passed $script:count assertions; evaluation only, no restore or native compilation. Evidence: $scratch"

#Requires -Version 7
[CmdletBinding()]
param(
    [string]$ReportDirectory,
    [switch]$D3DOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repositoryRoot '.local\reports'
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (-not (Test-Path -LiteralPath $ReportDirectory -PathType Container)) {
    throw "Register the report workspace with Manage-GeneratedWorkspace.ps1 before running: $ReportDirectory"
}
$pwsh = (Get-Process -Id $PID).Path
$memoryLimit = Join-Path $repositoryRoot 'scripts\dev\Invoke-WithMemoryLimit.ps1'
$outputDirectory = Join-Path $PSScriptRoot 'bin\win-x64'
$results = [Collections.Generic.List[object]]::new()
$started = [DateTime]::UtcNow

function Write-Summary {
    $summary = [ordered]@{
        startedUtc = $started.ToString('o')
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        allPassed = $results.Count -eq $(if ($D3DOnly) { 8 } else { 9 }) -and @($results | Where-Object exitCode -ne 0).Count -eq 0
        stages = @($results.ToArray())
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'validation-summary.json') -Encoding utf8
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Overlay idle release validation')
    $lines.Add('')
    foreach ($result in $results) {
        $lines.Add(('- {0}: exit {1}; stdout `{2}`; stderr `{3}`' -f $result.stage,
            $result.exitCode, $result.stdout, $result.stderr))
    }
    $lines | Set-Content -LiteralPath (Join-Path $ReportDirectory 'validation-summary.md') -Encoding utf8
}

function Invoke-Stage([string]$Name, [string]$FilePath, [string[]]$Arguments) {
    $stdout = Join-Path $ReportDirectory "$Name.stdout.log"
    $stderr = Join-Path $ReportDirectory "$Name.stderr.log"
    Write-Output "Starting $Name"
    $stageStarted = [DateTime]::UtcNow
    & $FilePath @Arguments 1> $stdout 2> $stderr
    $exitCode = $LASTEXITCODE
    $results.Add([pscustomobject]@{
        stage = $Name
        exitCode = $exitCode
        startedUtc = $stageStarted.ToString('o')
        endedUtc = [DateTime]::UtcNow.ToString('o')
        stdout = $stdout
        stderr = $stderr
    })
    Write-Summary
    Get-Content -LiteralPath $stdout | Write-Output
    Get-Content -LiteralPath $stderr | ForEach-Object { [Console]::Error.WriteLine($_) }
    Write-Output "Finished $Name with exit $exitCode"
}

Push-Location -LiteralPath $repositoryRoot
try {
    # This orchestration is submitted once to JobQueue; each test additionally owns a memory-limited child.
    $buildArguments = @('/d', '/c', (Join-Path $PSScriptRoot 'build.cmd'))
    if ($D3DOnly) { $buildArguments += 'd3d-only' }
    Invoke-Stage 'build-overlay' $env:ComSpec $buildArguments
    if ($results[-1].exitCode -ne 0) { exit $results[-1].exitCode }
    $buildArguments = @('/d', '/c', (Join-Path $PSScriptRoot 'build-test.cmd'))
    if ($D3DOnly) { $buildArguments += 'd3d-only' }
    Invoke-Stage 'build-tests' $env:ComSpec $buildArguments
    if ($results[-1].exitCode -ne 0) { exit $results[-1].exitCode }
    Invoke-Stage 'csharp-protocol' $pwsh @('-NoProfile', '-File',
        (Join-Path $PSScriptRoot 'Verify-OverlayProtocol.ps1'), '-ReportDirectory', $ReportDirectory)
    $tests = @(
        @{ name = 'd3d11'; executable = 'D3D11IdleReleaseTest.exe'; core = $false },
        @{ name = 'd3d12'; executable = 'D3D12IdleReleaseTest.exe'; core = $false },
        @{ name = 'gl-compatibility'; executable = 'GlIdleReleaseTest.exe'; core = $false },
        @{ name = 'gl-core'; executable = 'GlIdleReleaseTest.exe'; core = $true },
        @{ name = 'vulkan'; executable = 'VulkanIdleReleaseTest.exe'; core = $false }
    )
    if ($D3DOnly) {
        $tests = @($tests | Where-Object name -in @('d3d11', 'd3d12')) + @(
            @{ name = 'd3d12-baseline'; executable = 'D3D12IdleReleaseTest.exe'; core = $false; mode = '--baseline' },
            @{ name = 'd3d12-hooks-only'; executable = 'D3D12IdleReleaseTest.exe'; core = $false; mode = '--hooks-only' }
        )
    }
    $tests += @{ name = 'reader-timing'; executable = 'ReaderTimingTest.exe'; core = $false }
    foreach ($test in $tests) {
        $arguments = @('-NoProfile', '-File', $memoryLimit, '-FilePath', (Join-Path $outputDirectory $test.executable))
        if ($test.core) { $arguments += '-ArgumentList:--core' }
        if ($test.ContainsKey('mode')) { $arguments += "-ArgumentList:$($test.mode)" }
        Invoke-Stage $test.name $pwsh $arguments
    }
    if (@($results | Where-Object exitCode -ne 0).Count -gt 0) { exit 1 }
    exit 0
}
finally {
    Write-Summary
    Pop-Location
}

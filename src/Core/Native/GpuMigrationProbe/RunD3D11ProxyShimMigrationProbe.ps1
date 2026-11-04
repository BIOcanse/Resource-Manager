param(
    [string]$BinDir = "$PSScriptRoot\bin\win-x64",
    [string]$OutputDir = (Join-Path (Resolve-Path "$PSScriptRoot\..\..\..\..") "output"),
    [switch]$AllowUnsafeGpuMigrationProbe
)

$ErrorActionPreference = "Stop"

if (-not $AllowUnsafeGpuMigrationProbe) {
    throw "This manual probe loads a proxy shim and recreates a D3D11 device. Pass -AllowUnsafeGpuMigrationProbe explicitly; performance tests must not use it."
}

$probeSource = Join-Path $BinDir "ResourceManager.D3D11WindowReselectProbe.exe"
$shimSource = Join-Path $BinDir "shim\d3d11.dll"
if (-not (Test-Path -LiteralPath $probeSource)) {
    throw "Probe exe not found: $probeSource"
}
if (-not (Test-Path -LiteralPath $shimSource)) {
    throw "Shim dll not found: $shimSource"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$testDir = Join-Path $OutputDir "d3d11-proxy-shim-test"
New-Item -ItemType Directory -Force -Path $testDir | Out-Null

$probeExe = Join-Path $testDir "ResourceManager.D3D11WindowReselectProbe.exe"
$shimDll = Join-Path $testDir "d3d11.dll"
$policyFile = Join-Path $testDir "gpu-policy.txt"
$resultFile = Join-Path $OutputDir "d3d11-proxy-shim-migration.latest.json"

Copy-Item -LiteralPath $probeSource -Destination $probeExe -Force
Copy-Item -LiteralPath $shimSource -Destination $shimDll -Force
Set-Content -LiteralPath $policyFile -Value "default" -Encoding ASCII
Remove-Item -LiteralPath $resultFile -ErrorAction SilentlyContinue

$envBackup = [Environment]::GetEnvironmentVariable("RM_GPU_SHIM_POLICY_FILE", "Process")
$process = $null
try {
    [Environment]::SetEnvironmentVariable("RM_GPU_SHIM_POLICY_FILE", $policyFile, "Process")

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $probeExe
    $startInfo.WorkingDirectory = $testDir
    $startInfo.UseShellExecute = $false
    $arguments = @(
        "--duration-ms", "6500",
        "--auto-trigger-ms", "3000",
        "--auto-trigger-method", "customDeviceRecreate",
        "--allow-unsafe-migration-probe",
        "--output", $resultFile
    )
    $startInfo.Arguments = ($arguments | ForEach-Object {
        '"' + (($_ -replace '\\', '\\') -replace '"', '\"') + '"'
    }) -join ' '
    $startInfo.EnvironmentVariables["RM_GPU_SHIM_POLICY_FILE"] = $policyFile

    $process = [System.Diagnostics.Process]::Start($startInfo)
    Start-Sleep -Milliseconds 1400
    Set-Content -LiteralPath $policyFile -Value "lowPower" -Encoding ASCII

    Wait-Process -Id $process.Id -Timeout 15
    if (-not (Test-Path -LiteralPath $resultFile)) {
        throw "Probe did not write output: $resultFile"
    }

    Get-Content -LiteralPath $resultFile -Raw
}
finally {
    if ($process -ne $null) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    [Environment]::SetEnvironmentVariable("RM_GPU_SHIM_POLICY_FILE", $envBackup, "Process")
}

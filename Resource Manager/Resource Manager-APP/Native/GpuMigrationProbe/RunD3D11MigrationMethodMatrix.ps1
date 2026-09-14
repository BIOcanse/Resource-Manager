param(
    [string]$ProbeExe = "$PSScriptRoot\bin\win-x64\ResourceManager.D3D11WindowReselectProbe.exe",
    [string]$OutputDir = (Join-Path (Resolve-Path "$PSScriptRoot\..\..\..\..") "output"),
    [switch]$AllowUnsafeGpuMigrationProbe
)

$ErrorActionPreference = "Stop"

if (-not $AllowUnsafeGpuMigrationProbe) {
    throw "This manual probe changes GPU preference state and simulates device removal. Pass -AllowUnsafeGpuMigrationProbe explicitly; performance tests must not use it."
}

if (-not (Test-Path -LiteralPath $ProbeExe)) {
    throw "Probe exe not found: $ProbeExe"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$regPath = "HKCU:\Software\Microsoft\DirectX\UserGpuPreferences"
New-Item -Path $regPath -Force | Out-Null

function Backup-GpuPreference {
    param([string]$Exe)
    $prop = Get-ItemProperty -Path $regPath -Name $Exe -ErrorAction SilentlyContinue
    if ($null -eq $prop) {
        return [pscustomobject]@{ HadValue = $false; Value = $null }
    }

    return [pscustomobject]@{ HadValue = $true; Value = $prop.$Exe }
}

function Restore-GpuPreference {
    param([string]$Exe, [object]$Backup)
    if ($Backup.HadValue) {
        Set-ItemProperty -Path $regPath -Name $Exe -Value $Backup.Value
    } else {
        Remove-ItemProperty -Path $regPath -Name $Exe -ErrorAction SilentlyContinue
    }
}

function Invoke-ProbeCase {
    param(
        [string]$Name,
        [string[]]$ExtraArgs,
        [string]$Trigger
    )

    $backup = Backup-GpuPreference -Exe $ProbeExe
    $outFile = Join-Path $OutputDir "d3d11-migration-$Name.json"
    Remove-Item -LiteralPath $outFile -ErrorAction SilentlyContinue
    $process = $null

    try {
        Set-ItemProperty -Path $regPath -Name $ProbeExe -Value "GpuPreference=2;"
        $argumentList = @(
            "--duration-ms", "6500",
            "--auto-trigger-ms", "2600",
            "--auto-trigger-method", $Trigger,
            "--allow-unsafe-migration-probe",
            "--output", $outFile
        ) + $ExtraArgs

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $ProbeExe
        $startInfo.UseShellExecute = $false
        $startInfo.Arguments = ($argumentList | ForEach-Object {
            '"' + (($_ -replace '\\', '\\') -replace '"', '\"') + '"'
        }) -join ' '

        $process = [System.Diagnostics.Process]::Start($startInfo)
        Start-Sleep -Milliseconds 1200
        Set-ItemProperty -Path $regPath -Name $ProbeExe -Value "GpuPreference=1;"

        Wait-Process -Id $process.Id -Timeout 15
        if (-not (Test-Path -LiteralPath $outFile)) {
            throw "Probe did not write output for $Name"
        }

        $raw = Get-Content -LiteralPath $outFile -Raw
        $result = $raw | ConvertFrom-Json
        $result | Add-Member -NotePropertyName method -NotePropertyValue $Name
        $result | Add-Member -NotePropertyName trigger -NotePropertyValue $Trigger
        return $result
    }
    finally {
        if ($process -ne $null) {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }

        Restore-GpuPreference -Exe $ProbeExe -Backup $backup
    }
}

$cases = @(
    [pscustomobject]@{ Name = "redrawOnly"; ExtraArgs = @(); Trigger = "redrawOnly" },
    [pscustomobject]@{ Name = "displayChangeRecreate"; ExtraArgs = @("--recreate-on-display-change"); Trigger = "displayChange" },
    [pscustomobject]@{ Name = "settingChangeRecreate"; ExtraArgs = @("--recreate-on-setting-change"); Trigger = "settingChange" },
    [pscustomobject]@{ Name = "customDeviceRecreate"; ExtraArgs = @(); Trigger = "customDeviceRecreate" },
    [pscustomobject]@{ Name = "nextPaintRecreate"; ExtraArgs = @(); Trigger = "nextPaintRecreate" },
    [pscustomobject]@{ Name = "displayChangeExplicitLowPower"; ExtraArgs = @("--recreate-on-display-change", "--explicit-low-power-adapter-on-recreate"); Trigger = "displayChange" },
    [pscustomobject]@{ Name = "settingChangeExplicitLowPower"; ExtraArgs = @("--recreate-on-setting-change", "--explicit-low-power-adapter-on-recreate"); Trigger = "settingChange" },
    [pscustomobject]@{ Name = "customDeviceExplicitLowPower"; ExtraArgs = @("--explicit-low-power-adapter-on-recreate"); Trigger = "customDeviceRecreate" },
    [pscustomobject]@{ Name = "nextPaintExplicitLowPower"; ExtraArgs = @("--explicit-low-power-adapter-on-recreate"); Trigger = "nextPaintRecreate" },
    [pscustomobject]@{ Name = "presentDeviceRemoved"; ExtraArgs = @(); Trigger = "presentDeviceRemoved" },
    [pscustomobject]@{ Name = "presentDeviceRemovedExplicitLowPower"; ExtraArgs = @("--explicit-low-power-adapter-on-recreate"); Trigger = "presentDeviceRemoved" },
    [pscustomobject]@{ Name = "resizeBuffersDeviceRemoved"; ExtraArgs = @(); Trigger = "resizeBuffersDeviceRemoved" },
    [pscustomobject]@{ Name = "resizeBuffersDeviceRemovedExplicitLowPower"; ExtraArgs = @("--explicit-low-power-adapter-on-recreate"); Trigger = "resizeBuffersDeviceRemoved" },
    [pscustomobject]@{ Name = "minimizeRestoreNoActivate"; ExtraArgs = @(); Trigger = "minimizeRestoreNoActivate" },
    [pscustomobject]@{ Name = "minimizeRestoreNoActivateResizeBuffersDeviceRemoved"; ExtraArgs = @(); Trigger = "minimizeRestoreNoActivateResizeBuffersDeviceRemoved" },
    [pscustomobject]@{ Name = "minimizeRestoreNoActivateResizeBuffersDeviceRemovedExplicitLowPower"; ExtraArgs = @("--explicit-low-power-adapter-on-recreate"); Trigger = "minimizeRestoreNoActivateResizeBuffersDeviceRemoved" }
)

$results = foreach ($case in $cases) {
    Invoke-ProbeCase -Name $case.Name -ExtraArgs $case.ExtraArgs -Trigger $case.Trigger
}

$summaryPath = Join-Path $OutputDir "d3d11-migration-method-matrix.latest.json"
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

$residue = Get-ItemProperty -Path $regPath -Name $ProbeExe -ErrorAction SilentlyContinue
if ($null -ne $residue) {
    Write-Warning "Preference residue still exists for ${ProbeExe}: $($residue.$ProbeExe)"
}

Get-Content -LiteralPath $summaryPath -Raw

param(
    [string]$ResultPath,
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$brokerPath = Join-Path $scriptRoot "bin\win-x64\ResourceManager.GpuLaunchBroker.exe"
$targetPath = Join-Path $scriptRoot "bin\win-x64\ResourceManager.GpuLaunchTargetProbe.exe"
$ifeoRoot = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
$imageName = [IO.Path]::GetFileName($targetPath)
$ruleName = "ResourceManager-Probe"
$owner = "ResourceManager.GpuLaunchInterception.v1"

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path ([IO.Path]::GetTempPath()) "resource-manager-gpu-launch-probe-$([Guid]::NewGuid().ToString('N')).json"
}
$ResultPath = [IO.Path]::GetFullPath($ResultPath)

function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-WindowsArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            [void]$builder.Append(('\' * $slashes))
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) {
        [void]$builder.Append(('\' * ($slashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

if (-not $Elevated -and -not (Test-Administrator)) {
    $process = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -ResultPath `"$ResultPath`" -Elevated" `
        -Verb RunAs `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if (Test-Path -LiteralPath $ResultPath) {
        Get-Content -Raw -LiteralPath $ResultPath
    }
    exit $process.ExitCode
}

if (-not (Test-Administrator)) {
    throw "GPU launch interception probe requires an elevated process."
}
if (-not (Test-Path -LiteralPath $brokerPath) -or -not (Test-Path -LiteralPath $targetPath)) {
    throw "Build the GPU launch broker and probe before running this script."
}

$views = @(
    [Microsoft.Win32.RegistryView]::Registry64,
    [Microsoft.Win32.RegistryView]::Registry32
)

function Remove-ProbeRule([Microsoft.Win32.RegistryView]$View) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $View)
    try {
        $imagePath = "$ifeoRoot\$imageName"
        $image = $base.OpenSubKey($imagePath, $true)
        if ($null -eq $image) { return }
        try {
            $image.DeleteSubKeyTree($ruleName, $false)
            if ($image.GetValue("ResourceManagerUseFilterOwner") -eq $owner) {
                $hasFullPath = $false
                foreach ($subName in $image.GetSubKeyNames()) {
                    $sub = $image.OpenSubKey($subName, $false)
                    try {
                        if ($null -ne $sub -and -not [string]::IsNullOrWhiteSpace([string]$sub.GetValue("FilterFullPath"))) {
                            $hasFullPath = $true
                            break
                        }
                    }
                    finally {
                        if ($null -ne $sub) { $sub.Dispose() }
                    }
                }
                if (-not $hasFullPath) { $image.DeleteValue("UseFilter", $false) }
                $image.DeleteValue("ResourceManagerUseFilterOwner", $false)
            }
        }
        finally {
            $image.Dispose()
        }
        $image = $base.OpenSubKey($imagePath, $false)
        try {
            if ($null -ne $image `
                -and $image.GetSubKeyNames().Count -eq 0 `
                -and $image.GetValueNames().Count -eq 0) {
                $image.Dispose()
                $image = $null
                $base.DeleteSubKeyTree($imagePath, $false)
            }
        }
        finally {
            if ($null -ne $image) { $image.Dispose() }
        }
    }
    finally {
        $base.Dispose()
    }
}

function Add-ProbeRule([Microsoft.Win32.RegistryView]$View) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $View)
    try {
        $image = $base.CreateSubKey("$ifeoRoot\$imageName", $true)
        try {
            if ($null -eq $image.GetValue("UseFilter")) {
                try { $image.SetValue("ResourceManagerUseFilterOwner", $owner, [Microsoft.Win32.RegistryValueKind]::String) }
                catch { throw "parent owner marker write failed: $($_.Exception.Message)" }
            }
            try { $image.SetValue("UseFilter", 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
            catch { throw "UseFilter write failed: $($_.Exception.Message)" }
            $rule = $image.CreateSubKey($ruleName, $true)
            try {
                try { $rule.SetValue("ResourceManagerOwner", $owner, [Microsoft.Win32.RegistryValueKind]::String) }
                catch { throw "rule owner marker write failed: $($_.Exception.Message)" }
                try { $rule.SetValue("ResourceManagerRuleId", $ruleName, [Microsoft.Win32.RegistryValueKind]::String) }
                catch { throw "rule id write failed: $($_.Exception.Message)" }
                try { $rule.SetValue("FilterFullPath", $targetPath, [Microsoft.Win32.RegistryValueKind]::String) }
                catch { throw "FilterFullPath write failed: $($_.Exception.Message)" }
                try { $rule.SetValue("Debugger", "`"$brokerPath`" --resource-manager-ifeo", [Microsoft.Win32.RegistryValueKind]::String) }
                catch { throw "Debugger write failed: $($_.Exception.Message)" }
            }
            finally {
                $rule.Dispose()
            }
        }
        finally {
            $image.Dispose()
        }
    }
    finally {
        $base.Dispose()
    }
}

$probeRoot = Join-Path ([IO.Path]::GetTempPath()) "ResourceManager-GpuLaunchProbe-$([Guid]::NewGuid().ToString('N'))"
$outputPath = Join-Path $probeRoot "target-output.txt"
$result = [ordered]@{
    success = $false
    exitCode = $null
    outputPath = $outputPath
    checks = [ordered]@{}
    error = $null
}

try {
    New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
    foreach ($view in $views) {
        try {
            Remove-ProbeRule $view
            Add-ProbeRule $view
        }
        catch {
            throw "IFEO $view registration failed: $($_.Exception.Message)"
        }
    }

    $env:RM_GPU_LAUNCH_PROBE_OUTPUT = $outputPath
    $env:RM_GPU_LAUNCH_PROBE_VALUE = "environment-preserved"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $targetPath
    $start.WorkingDirectory = $probeRoot
    $start.UseShellExecute = $false
    $start.Arguments = (@(
        "plain",
        "two words",
        'quote"inside',
        "trailing\"
    ) | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit(15000) | Out-Null
    if (-not $process.HasExited) {
        $process.Kill($true)
        throw "Probe launch did not exit within 15 seconds."
    }

    $result.exitCode = $process.ExitCode
    $result.checks.exitCodeMirrored = $process.ExitCode -eq 37
    $result.checks.outputCreated = Test-Path -LiteralPath $outputPath
    if ($result.checks.outputCreated) {
        $lines = Get-Content -LiteralPath $outputPath -Encoding Unicode
        $values = @{}
        foreach ($line in $lines) {
            $separator = $line.IndexOf('=')
            if ($separator -gt 0) {
                $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
            }
        }
        $result.checks.argumentCount = $values.argc -eq "5"
        $result.checks.argumentsPreserved = $values.argv1 -eq "plain" `
            -and $values.argv2 -eq "two words" `
            -and $values.argv3 -eq 'quote"inside' `
            -and $values.argv4 -eq "trailing\"
        $result.checks.workingDirectoryPreserved = $values.cwd -eq $probeRoot
        $result.checks.environmentPreserved = $values.probeEnvironment -eq "environment-preserved"
    }
    $result.checks.noBrokerRemaining = @(Get-Process -Name "ResourceManager.GpuLaunchBroker" -ErrorAction SilentlyContinue).Count -eq 0
    $result.checks.noTargetRemaining = @(Get-Process -Name "ResourceManager.GpuLaunchTargetProbe" -ErrorAction SilentlyContinue).Count -eq 0
    $result.success = @($result.checks.Values | Where-Object { $_ -ne $true }).Count -eq 0
}
catch {
    $result.error = $_.Exception.Message
}
finally {
    Remove-Item Env:RM_GPU_LAUNCH_PROBE_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item Env:RM_GPU_LAUNCH_PROBE_VALUE -ErrorAction SilentlyContinue
    foreach ($view in $views) {
        try { Remove-ProbeRule $view } catch {}
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

$result | ConvertTo-Json -Depth 5
exit $(if ($result.success) { 0 } else { 1 })

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [switch]$Elevated
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'

$taskRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
$marker = Join-Path $taskRoot '.generated-workspace.json'
if (-not [IO.File]::Exists($marker) -or (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).state -ne 'running') {
    throw 'WorkRoot must be a running generated workspace.'
}
$fixture = Join-Path $taskRoot 'backend-key-fixture'
$receipt = Join-Path $taskRoot 'backend-key-fixture-setup.json'

function Assert-PlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $fixture -and -not $full.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture path escaped the registered task fixture.'
    }
    $cursor = $full
    $count = 0
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (++$count -gt 513) { throw 'Fixture ancestor count exceeds its bound.' }
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Fixture path contains a reparse point: $cursor"
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

Assert-PlainPath $fixture
if (-not $Elevated) {
    try {
        $hostExe = (Get-Process -Id $PID).Path
        # Only an encoded PowerShell command crosses cmd.exe. Fixture paths
        # remain quoted PowerShell literals and cannot become cmd operators.
        $command = "& '" + $PSCommandPath.Replace("'", "''") + "' -WorkRoot '" + $taskRoot.Replace("'", "''") + "' -Elevated"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $arguments = '/d /s /c ""' + $hostExe + '" -NoProfile -EncodedCommand ' + $encoded + '"'
        $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/cmd.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "Elevated fixture preparation failed with exit code $($process.ExitCode)."
        }
    } catch {
        @{ status = 'failed-setup'; fixture = $fixture; error = $_.Exception.Message; hresult = $_.Exception.HResult } |
            ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
        Get-Content -LiteralPath $receipt
        exit 1
    }
    Get-Content -LiteralPath $receipt
    exit 0
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Fixture preparation requires an administrator token.'
    }
    foreach ($variant in @('', 'untrusted')) {
        $package = if ($variant) { Join-Path $fixture $variant } else { $fixture }
        $directory = Join-Path $package 'UserData/Security'
        $file = Join-Path $directory 'default-key'
        Assert-PlainPath $directory
        Assert-PlainPath $file
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        [IO.File]::WriteAllText($file, ('rm_sk_' + ('abcdef0123456789' * 4)), [Text.UTF8Encoding]::new($false))
        $fileAcl = [Security.AccessControl.FileSecurity]::new()
        $rights = if ($variant) { 'FA' } else { 'FR' }
        $fileAcl.SetSecurityDescriptorSddlForm("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;$rights;;;BU)")
        Set-Acl -LiteralPath $file -AclObject $fileAcl
        $directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
        $directoryAcl.SetSecurityDescriptorSddlForm('O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200a0;;;BU)')
        Set-Acl -LiteralPath $directory -AclObject $directoryAcl
    }
    @{ status = 'prepared'; fixture = $fixture; caller = $identity.Name; variants = @('trusted', 'untrusted-file-acl') } |
        ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
} catch {
    @{ status = 'failed-setup'; fixture = $fixture; error = $_.Exception.Message; hresult = $_.Exception.HResult } |
        ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
    exit 1
}

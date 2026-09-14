[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$ProcessId,
    [Parameter(Mandatory=$true)][long]$CreationFileTimeUtc,
    [Parameter(Mandatory=$true)][string]$ImagePath,
    [Parameter(Mandatory=$true)][string]$ReceiptPath,
    [ValidateRange(1,60000)][int]$TimeoutMilliseconds = 30000,
    [switch]$RequireKernelEtw
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exitCode = 1
$receipt = $null
try {
    if (-not [IO.Path]::IsPathRooted($ReceiptPath)) { throw 'The receipt path must be absolute.' }
    $output = [IO.Path]::GetFullPath($ReceiptPath)
    if ((Test-Path -LiteralPath $output) -or -not (Test-Path -LiteralPath (Split-Path -Parent $output) -PathType Container)) {
        throw 'The receipt requires an existing parent and a fresh filename.'
    }
    $inputs = @($PSCommandPath, (Join-Path $PSScriptRoot 'ConsoleStop.cs'), (Join-Path $PSScriptRoot 'KernelEtwSnapshot.cs'))
    $identities = @($inputs | ForEach-Object {
        [pscustomobject]@{ path=$_; byteLength=(Get-Item -LiteralPath $_).Length; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    Add-Type -Path $inputs[1], $inputs[2]
    [void][ResourceManager.Tools.BackendLifetime.ConsoleStop]::SetErrorMode(32771)
    $receipt = [ordered]@{
        contract='resource-manager-owned-backend-console-stop-v1'; passed=$false
        senderProcessId=$PID
        senderCreationFileTimeUtc=[Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc()
        requestedProcessId=$ProcessId; requestedCreationFileTimeUtc=$CreationFileTimeUtc
        imagePath=[IO.Path]::GetFullPath($ImagePath); timeoutMilliseconds=$TimeoutMilliseconds
        inputIdentities=$identities; errorMode=[ResourceManager.Tools.BackendLifetime.ConsoleStop]::GetErrorMode()
        before=$null; stop=$null; after=$null; error=$null; forced=$false
    }
    if ($receipt.errorMode -ne 32771) { throw 'The stop launch edge is not guarded.' }
    $sessionName = 'ResourceManagerKernelTelemetry-' + $ProcessId
    $receipt.before = [ResourceManager.Tools.BackendLifetime.KernelEtwSnapshot]::Read($sessionName)
    if ($RequireKernelEtw -and $null -eq $receipt.before) { throw 'The required backend kernel ETW session is absent before stop.' }
    $receipt.stop = [ResourceManager.Tools.BackendLifetime.ConsoleStop]::Request(
        $ProcessId, $CreationFileTimeUtc, $ImagePath, $TimeoutMilliseconds)
    $receipt.after = [ResourceManager.Tools.BackendLifetime.KernelEtwSnapshot]::Read($sessionName)
    if (-not $receipt.stop.Exited) { throw 'The backend did not stop within its shutdown deadline.' }
    if ($receipt.stop.ExitCode -ne 0) { throw 'The backend returned a nonzero exit code.' }
    if ($null -ne $receipt.after) { throw 'The backend left a kernel ETW session after exit.' }
    foreach ($identity in $identities) {
        if ((Get-FileHash -LiteralPath $identity.path -Algorithm SHA256).Hash -cne $identity.sha256) {
            throw 'A stop control changed during execution.'
        }
    }
    $receipt.passed = $true
    $exitCode = 0
}
catch {
    if ($null -eq $receipt) { [Console]::Error.WriteLine($_.Exception.Message) }
    else { $receipt.error = $_.Exception.Message }
}
if ($null -ne $receipt) {
    $receipt.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $json = $receipt | ConvertTo-Json -Depth 10
    $stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
    $json
}
exit $exitCode

[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$DownloadDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $directory = [IO.Path]::GetFullPath($DownloadDirectory)
    $null = [IO.Directory]::CreateDirectory($directory)
    $download = Join-Path $directory 'MicrosoftEdgeWebview2Setup.download.exe'
    $installer = Join-Path $directory 'MicrosoftEdgeWebview2Setup.exe'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' `
        -OutFile $download -UseBasicParsing -TimeoutSec 120
    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw 'The downloaded WebView2 installer does not have a valid Microsoft signature.'
    }
    Move-Item -LiteralPath $download -Destination $installer -Force
    $process = Start-Process -FilePath $installer -ArgumentList @('/silent', '/install') `
        -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(900000)) {
            throw 'WebView2 setup is still running. Allow it to finish before starting Resource Manager again.'
        }
        if ($process.ExitCode -ne 0) {
            throw "WebView2 setup exited with code $($process.ExitCode)."
        }
    }
    finally { $process.Dispose() }
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][string]$InputManifest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
$utf8 = [Text.UTF8Encoding]::new($false)
$inputs = Get-Content -LiteralPath $InputManifest -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath($OutputRoot)
if (-not [IO.Directory]::Exists($output) -or -not [IO.File]::Exists($CompilerPath)) { throw 'Existing bounded output and compiler are required.' }
if (@($inputs.tests).Count -lt 1 -or @($inputs.tests).Count -gt 32) { throw 'Native test count exceeds the bound.' }
$producer = Join-Path $output 'producer'
[IO.Directory]::CreateDirectory($producer) | Out-Null
$sourceRows = [Collections.Generic.List[object]]::new()
$files = @(Get-ChildItem -LiteralPath $inputs.librarySourceDirectory -File |
    Where-Object { $_.Extension -in @('.c','.h','.inc','.cmd') })
if ($files.Count -lt 1 -or $files.Count -gt 64 -or ($files | Measure-Object Length -Sum).Sum -gt 2MB) { throw 'Native producer projection exceeds the bound.' }
foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Native source cannot be a reparse point.' }
    $before = (Get-FileHash -LiteralPath $file.FullName).Hash
    $target = Join-Path $producer $file.Name
    [IO.File]::Copy($file.FullName, $target, $false)
    if ((Get-FileHash -LiteralPath $target).Hash -cne $before -or (Get-FileHash -LiteralPath $file.FullName).Hash -cne $before) { throw 'Native source changed during projection.' }
    $sourceRows.Add([pscustomobject]@{source=$file.FullName; projected=$target; sha256=$before; length=$file.Length})
}
# Run the original product recipe, with the selected gcc first on PATH.
# cmd.exe drops an inherited PATH longer than its 8191-character limit.
# The product recipe only needs gcc and the Windows command tools.
$env:Path = (Split-Path -Parent $CompilerPath) + ';' + $env:SystemRoot + '\System32;' + $env:SystemRoot
if ([IO.Path]::GetFullPath((Get-Command gcc).Source) -ine [IO.Path]::GetFullPath($CompilerPath)) { throw 'Product recipe resolved another compiler.' }
Push-Location $producer
try { & cmd.exe /d /c .\build.cmd; if ($LASTEXITCODE -ne 0) { throw "Product native recipe failed: $LASTEXITCODE" } }
finally { Pop-Location }
$tests = [Collections.Generic.List[object]]::new()
foreach ($source in @($inputs.tests)) {
    $file = Get-Item -LiteralPath $source
    if ($file.Extension -ne '.c' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A regular C fixture is required.' }
    $executable = Join-Path $output ($file.BaseName + '.exe')
    & $CompilerPath -O2 -Wall -Wextra -static-libgcc $file.FullName -o $executable
    if ($LASTEXITCODE -ne 0) { throw "Native fixture compile failed: $($file.Name)" }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "Native fixture failed: $($file.Name), exit $LASTEXITCODE" }
    $tests.Add([pscustomobject]@{source=$file.FullName;sourceSha256=(Get-FileHash $file.FullName).Hash;executable=$executable;sha256=(Get-FileHash $executable).Hash;exitCode=0})
}
$library = Join-Path $producer $inputs.libraryRelativePath
if (-not [IO.File]::Exists($library)) { throw 'Product recipe did not produce the declared library.' }
$result = [ordered]@{accepted=$true;library=$library;librarySha256=(Get-FileHash $library).Hash;compiler=$CompilerPath;compilerSha256=(Get-FileHash $CompilerPath).Hash;producer=$sourceRows;tests=$tests;hardwareIO=$false}
[IO.File]::WriteAllText((Join-Path $output 'native-qualification.json'), ($result | ConvertTo-Json -Depth 12), $utf8)

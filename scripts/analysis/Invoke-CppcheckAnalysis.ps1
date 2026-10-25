#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [Parameter(Mandatory)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    $OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
    $exe = 'D:\Code\CommonAssets\Tools\Cppcheck\2.22.0\PFiles\Cppcheck\cppcheck.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Pinned cppcheck executable is missing: $exe" }
    (Get-Process -Id $PID).PriorityClass = 'BelowNormal'
    $version = (& $exe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -cne 'Cppcheck 2.22.0') { throw "Expected Cppcheck 2.22.0, got '$version'." }
    $helpText = (& $exe --help | Out-String)
    if ($LASTEXITCODE -ne 0 -or $helpText -notmatch '(?m)^\s+\* sarif\s*$') { throw 'Pinned cppcheck does not advertise SARIF output.' }
    # OutputRoot belongs to a workspace registered by the caller; lifecycle stays with the caller.
    [void][IO.Directory]::CreateDirectory($OutputRoot)
    $sources = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($root in @('src/Core/Native', 'tests/Native')) { $pending.Enqueue((Join-Path $RepoRoot $root)) }
    $directories = 0
    while ($pending.Count -gt 0) {
        if (++$directories -gt 10000) { throw 'Native traversal exceeded 10000 directories.' }
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($entry.PSIsContainer) {
                if ($entry.Name -ne 'third_party') { $pending.Enqueue($entry.FullName) }
            } elseif ($entry.Extension -in @('.c', '.cc', '.cpp', '.cxx', '.c++')) {
                $sources.Add([IO.Path]::GetRelativePath($RepoRoot, $entry.FullName).Replace('\', '/'))
            }
        }
    }
    if ($sources.Count -eq 0) { throw 'No native translation units were found.' }
    $sourceList = @($sources | Sort-Object)
    $fileList = Join-Path $OutputRoot 'inputs.txt'
    [IO.File]::WriteAllLines($fileList, [string[]]$sourceList, [Text.UTF8Encoding]::new($false))
    $cache = Join-Path $OutputRoot 'cache'
    [void][IO.Directory]::CreateDirectory($cache)
    $arguments = @(
        '--enable=warning,performance,portability', '--platform=win64', '--std=c++17',
        '--library=windows', '--library=microsoft_sal', '--max-configs=64', '-j', '2',
        '-Isrc/Core/Native/GpuPlacementShim/third_party/minhook/include',
        '-Isrc/Core/Native/GpuPlacementShim/third_party/vulkan-headers/include',
        '-Isrc/Core/Native/GpuPlacementShim/third_party/detours/src',
        '-D_WIN32=1', '-D_WIN64=1', '-D_M_X64=100', '-D__x86_64__=1',
        '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00',
        '--suppress=*:*/third_party/*', '--config-exclude=src/Core/Native/GpuPlacementShim/third_party',
        '--error-exitcode=2', '--verbose',
        "--relative-paths=$RepoRoot", "--file-list=$fileList", "--cppcheck-build-dir=$cache"
    )
    $receipt = [ordered]@{
        tool = 'cppcheck'; version = $version; platform = 'win64'; priority = 'BelowNormal'
        sourceCount = $sourceList.Count; sources = $sourceList; configurationLimit = 64; workers = 2
        defines = @('_WIN32=1', '_WIN64=1', '_M_X64=100', '__x86_64__=1', 'WINVER=0x0A00', '_WIN32_WINNT=0x0A00')
        includeDirectories = @('src/Core/Native/GpuPlacementShim/third_party/minhook/include', 'src/Core/Native/GpuPlacementShim/third_party/vulkan-headers/include', 'src/Core/Native/GpuPlacementShim/third_party/detours/src')
        libraries = @('windows', 'microsoft_sal'); excluded = @('**/third_party/**', 'reparse points')
        scope = 'First-party C/C++ translation units and included headers; Windows SDK headers modeled with libraries, not supplied as source.'
        startedUtc = [DateTime]::UtcNow.ToString('o'); passes = @()
    }
    Push-Location -LiteralPath $RepoRoot
    try {
        & $exe @arguments '--check-config' '--enable=information,missingInclude' '--output-format=xmlv2' "--output-file=$(Join-Path $OutputRoot 'configuration.xml')" *> (Join-Path $OutputRoot 'configuration.log')
        $configurationExit = $LASTEXITCODE
        $receipt.configurationExitCode = $configurationExit
        if ($configurationExit -notin @(0, 2)) { throw "cppcheck configuration check failed with exit $configurationExit" }
        foreach ($format in @('xmlv2', 'sarif')) {
            $extension = if ($format -eq 'xmlv2') { 'xml' } else { 'sarif' }
            $output = Join-Path $OutputRoot "cppcheck.$extension"
            $log = Join-Path $OutputRoot "cppcheck-$format.log"
            & $exe @arguments "--output-format=$format" "--output-file=$output" "--checkers-report=$(Join-Path $OutputRoot 'checkers.txt')" *> $log
            $toolExit = $LASTEXITCODE
            $receipt.passes += [ordered]@{ format = $format; exitCode = $toolExit }
            if ($toolExit -notin @(0, 2)) { throw "cppcheck $format failed with exit $toolExit; see $log" }
            if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "cppcheck did not create $output" }
        }
    } finally { Pop-Location }
    $sarif = Get-Content -LiteralPath (Join-Path $OutputRoot 'cppcheck.sarif') -Raw | ConvertFrom-Json
    if ($sarif.version -ne '2.1.0') { throw "Unexpected SARIF version: $($sarif.version)" }
    [xml]$xml = Get-Content -LiteralPath (Join-Path $OutputRoot 'cppcheck.xml') -Raw
    $receipt.findings = @($xml.SelectNodes('/results/errors/error')).Count
    $receipt.completedUtc = [DateTime]::UtcNow.ToString('o')
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'receipt.json') -Encoding utf8NoBOM
    Write-Output "Cppcheck completed: $($sourceList.Count) translation units, $($receipt.findings) XML findings; SARIF 2.1.0 verified."
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    throw
}

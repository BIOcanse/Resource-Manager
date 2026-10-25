[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..'),
    [Parameter(Mandatory)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    (Get-Process -Id $PID).PriorityClass = 'BelowNormal'
    $repo = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $output = [IO.Path]::GetFullPath($OutputRoot)
    $modulePath = 'D:/Code/CommonAssets/Tools/PowerShellModules/PSScriptAnalyzer/1.24.0/PSScriptAnalyzer.psd1'
    $module = Import-Module -Name $modulePath -PassThru -Force
    if ($module.Version -ne [version]'1.24.0') { throw 'PSScriptAnalyzer 1.24.0 is required.' }
    [void][IO.Directory]::CreateDirectory($output)
    $files = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('scripts', 'src', 'tests', 'sdk')) {
        $path = Join-Path $repo $name
        if (Test-Path -LiteralPath $path -PathType Container) { $pending.Enqueue($path) }
    }
    $excluded = @('node_modules', 'bin', 'obj', '.git', '.local', 'third_party', 'NonAdaptedOptimizationLab', 'dist', 'out', 'packages', 'artifacts', '.next', 'coverage')
    $nodes = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        if (-not $seen.Add($directory)) { continue }
        $nodes++
        if ($nodes -gt 100000) { throw 'Directory scan exceeded 100000 nodes.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            $relative = [IO.Path]::GetRelativePath($repo, $entry.FullName).Replace('\', '/')
            if ($entry.PSIsContainer) {
                if ($entry.Name -in $excluded -or $relative -eq 'scripts/analysis' -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
                $pending.Enqueue($entry.FullName)
            } elseif ($entry.Extension -eq '.ps1' -or ($relative.StartsWith('scripts/') -and $entry.Extension -in @('.psm1', '.psd1'))) {
                $files.Add($entry.FullName)
            }
        }
    }
    $findings = [Collections.Generic.List[object]]::new()
    foreach ($file in ($files | Sort-Object)) {
        $relative = [IO.Path]::GetRelativePath($repo, $file).Replace('\', '/')
        foreach ($diagnostic in @(Invoke-ScriptAnalyzer -Path $file)) {
            $findings.Add([ordered]@{
                tool = 'PSScriptAnalyzer'; rule = [string]$diagnostic.RuleName
                severity = ([string]$diagnostic.Severity).ToLowerInvariant(); file = $relative
                line = [Math]::Max(1, $diagnostic.Line)
                column = [Math]::Max(1, $diagnostic.Column)
                message = ([string]$diagnostic.Message).Replace($repo + '\', '').Replace($repo + '/', '')
            })
        }
    }
    $results = @($findings | ForEach-Object {
        $level = switch ($_.severity) { 'error' { 'error' } 'warning' { 'warning' } default { 'note' } }
        [ordered]@{ ruleId = $_.rule; level = $level; message = @{ text = $_.message }
            locations = @(@{ physicalLocation = @{ artifactLocation = @{ uri = $_.file }; region = @{ startLine = $_.line; startColumn = $_.column } } }) }
    })
    $rules = @($findings | ForEach-Object { $_.rule } | Sort-Object -Unique | ForEach-Object { @{ id = $_; name = $_ } })
    $sarif = [ordered]@{
        '$schema' = 'https://json.schemastore.org/sarif-2.1.0.json'; version = '2.1.0'
        runs = @(@{ tool = @{ driver = @{ name = 'PSScriptAnalyzer'; version = '1.24.0'; rules = $rules } }; results = $results })
    }
    ConvertTo-Json -InputObject @($findings.ToArray()) -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'findings.json') -Encoding utf8NoBOM
    $sarif | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'findings.sarif') -Encoding utf8NoBOM
    [ordered]@{ tool = 'PSScriptAnalyzer'; version = '1.24.0'; hostVersion = $PSVersionTable.PSVersion.ToString(); rules = 'default'; fileCount = $files.Count; findingCount = $findings.Count; excludedDirectoryNames = $excluded; infrastructureExcluded = 'scripts/analysis'; files = @($files | Sort-Object | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_).Replace('\', '/') }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8NoBOM
    Write-Output ('PSScriptAnalyzer 1.24.0: {0} files, {1} findings.' -f $files.Count, $findings.Count)
} catch {
    throw
}

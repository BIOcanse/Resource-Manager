[CmdletBinding()]
param([Parameter(Mandatory)][string]$RepoRoot,[Parameter(Mandatory)][string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$repo = [IO.Path]::GetFullPath($RepoRoot)
$output = [IO.Path]::GetFullPath($OutputRoot)
Set-Location -LiteralPath $repo
                $zig = (Get-Command zig -CommandType Application).Source
                $version = & $zig version
                if ($LASTEXITCODE -ne 0 -or $version -ne '0.16.0') { throw 'Expected Zig 0.16.0 on launcher PATH' }
                $pending = [Collections.Generic.Queue[string]]::new()
                foreach ($root in @('src/Core/NativeCore','sdk/zig')) { $pending.Enqueue((Join-Path $repo $root)) }
                $zigFiles = [Collections.Generic.List[string]]::new()
                $directories = 0
                while ($pending.Count) {
                    if (++$directories -gt 10000) { throw 'Zig traversal exceeded 10000 directories' }
                    foreach ($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                        if ($entry.PSIsContainer) {
                            if ($entry.Name -notin @('third_party','zig-out','bin','obj','node_modules') -and $entry.Name -notlike '.zig-*') { $pending.Enqueue($entry.FullName) }
                        } elseif ($entry.Extension -eq '.zig') { $zigFiles.Add([IO.Path]::GetRelativePath($repo,$entry.FullName).Replace('\','/')) }
                    }
                }
                $files = @($zigFiles | Sort-Object)
                if ($files.Count -eq 0) { throw 'Zig source enumeration found no files' }
                $results = foreach ($file in $files) {
                    $diagnostics = @(& $zig ast-check $file 2>&1 | ForEach-Object { "$_" })
                    $exit = $LASTEXITCODE
                    [pscustomobject]@{file=$file;exitCode=$exit;diagnostics=$diagnostics}
                }
                [pscustomobject]@{version=$version;files=@($results)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$output/zig.json"


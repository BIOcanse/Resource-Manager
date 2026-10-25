param([string]$SourceRepository, [string]$SnapshotOutput)

function Get-StaticAnalysisInputs([string]$Repository) {
    $paths = @(& git -C $Repository -c core.quotePath=false ls-files -- '*.cs' '*.csproj' '*.props' '*.targets' '*.sln' '*.slnx' '.editorconfig' '*.globalconfig' 'NuGet.Config')
    if ($LASTEXITCODE -ne 0 -or $paths.Count -gt 50000) { throw 'Cannot enumerate bounded tracked C# analysis inputs.' }
    # This checked-out generated source is explicitly included by NativeUi and
    # can be seen by no-build extraction even though Git ignores it.
    $frontendValues='src/UI/Core/Preferences/Generated/FrontendPoints.Values.g.cs'
    if(Test-Path -LiteralPath (Join-Path $Repository $frontendValues)) { $paths+= $frontendValues }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $records=[Collections.Generic.List[object]]::new()
    foreach ($path in ($paths | Sort-Object -Unique)) {
        if ($path -match '^tests/Validation/NonAdaptedOptimizationLab/|^scripts/analysis/') { continue }
        if ($timer.Elapsed.TotalSeconds -gt 60) { throw 'Analysis source fingerprint exceeded 60 seconds.' }
        $fullPath=[IO.Path]::Combine($Repository,$path)
        if ([IO.File]::GetAttributes($fullPath) -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse source is not supported: $path" }
        $stream=[IO.File]::OpenRead($fullPath)
        try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $records.Add([pscustomobject]@{path=$path; sha256=$hash})
    }
    $json=ConvertTo-Json -InputObject $records.ToArray() -Compress -Depth 4
    $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json)))
    return [pscustomobject]@{sha256=$digest; count=$records.Count; files=$records.ToArray(); excludes=@('tests/Validation/NonAdaptedOptimizationLab','scripts/analysis','other untracked outputs')}
}

if($SnapshotOutput) {
    if(-not $SourceRepository) { throw '-SourceRepository is required with -SnapshotOutput.' }
    Get-StaticAnalysisInputs $SourceRepository | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $SnapshotOutput -Encoding utf8
}

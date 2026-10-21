[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TargetPath,
    [Parameter(Mandatory)][ValidateSet('build','before-tests','after-tests')][string]$Label,
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$ReuseEvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($TargetPath) -or -not [IO.File]::Exists($TargetPath)){throw 'The actual compiled test DLL is missing.'}
$reuseRows=@()
if(-not [string]::IsNullOrWhiteSpace($ReuseEvidencePath)) {
    if(-not [IO.Path]::IsPathRooted($ReuseEvidencePath) -or -not [IO.File]::Exists($ReuseEvidencePath) -or
        ([IO.FileInfo]::new($ReuseEvidencePath)).Length -gt 1MB){throw 'The original Build output evidence is missing or exceeds its bound.'}
    $decoded=Get-Content -LiteralPath $ReuseEvidencePath -Raw|ConvertFrom-Json
    $reuseRows=@($decoded)
    if($reuseRows.Count -gt 16){throw 'The original Build output evidence exceeds its row bound.'}
}
$evidence=Join-Path $RunDirectory ('app-test-output-'+$Label)
$base=[IO.Path]::Combine([IO.Path]::GetDirectoryName($TargetPath),[IO.Path]::GetFileNameWithoutExtension($TargetPath))
$rows=@(foreach($suffix in @('.dll','.pdb','.deps.json','.runtimeconfig.json')) {
    $file=$base+$suffix
    if([IO.File]::Exists($file)) {
        $length=([IO.FileInfo]::new($file)).Length
        if($length -gt 67108864){throw "Unexpectedly large App test evidence file: $file"}
        $hash=(Get-FileHash -LiteralPath $file).Hash
        $previous=@($reuseRows|Where-Object {$_.exists -and $_.path -ieq $file})
        if($previous.Count -gt 1){throw 'Original Build output evidence contains duplicate file rows.'}
        $reuse=$previous.Count -eq 1 -and $previous[0].length -eq $length -and $previous[0].sha256 -ieq $hash
        if($reuse) {
            $archive=[string]$previous[0].archive
            if(-not [IO.Path]::IsPathRooted($archive) -or -not [IO.File]::Exists($archive) -or
                ([IO.FileInfo]::new($archive)).Length -ne $length -or (Get-FileHash -LiteralPath $archive).Hash -ine $hash){throw 'The original Build archive changed.'}
        } else {
            # Build stores one copy. Runtime groups reuse it when identical;
            # changed or newly appearing outputs retain only their own bytes.
            [void][IO.Directory]::CreateDirectory($evidence)
            $archive=Join-Path $evidence ([IO.Path]::GetFileName($file))
            Copy-Item -LiteralPath $file -Destination $archive
            if((Get-FileHash -LiteralPath $archive).Hash -cne $hash){throw 'Test output evidence copy changed.'}
        }
        [pscustomobject]@{path=$file;exists=$true;length=$length;sha256=$hash;archive=$archive;reusedBuildArchive=$reuse}
    } else { [pscustomobject]@{path=$file;exists=$false} }
})
[IO.File]::WriteAllText((Join-Path $RunDirectory ('test-output-'+$Label+'.json')),($rows|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
$rows

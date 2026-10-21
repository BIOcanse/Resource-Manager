[CmdletBinding()]
param(
    [AllowEmptyCollection()][string[]]$References=@(),
    [Parameter(Mandatory)][object]$Policy,
    [AllowEmptyCollection()][string[]]$ReferenceRoots=@()
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if($References.Count -gt 750000){throw 'Lifecycle reference input exceeds the 750000-entry bound.'}
$rootSet=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($configured in @($Policy.roots)) {
    $root=[IO.Path]::GetFullPath(([string]$configured.path).Replace('{RepositoryRoot}',$repo))
    [void]$rootSet.Add($root.TrimEnd('\'))
}
foreach($configured in $ReferenceRoots) {
    if(-not [string]::IsNullOrWhiteSpace($configured)) {
        if(-not [IO.Path]::IsPathRooted($configured)){throw 'Reference roots must be absolute paths.'}
        [void]$rootSet.Add([IO.Path]::GetFullPath($configured).TrimEnd('\'))
    }
}
if($rootSet.Count -gt 4096){throw 'Lifecycle reference roots exceed the 4096-root bound.'}
$roots=@($rootSet|Sort-Object -Property Length -Descending)
$sourceRoots=@('src','tests','scripts','sdk','docs'|ForEach-Object {Join-Path $repo $_})
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($reference in $References) {
    if([string]::IsNullOrWhiteSpace($reference) -or -not [IO.Path]::IsPathRooted($reference)){throw 'Lifecycle references must be nonempty absolute paths.'}
    $path=[IO.Path]::GetFullPath($reference)
    if($path.Equals([IO.Path]::GetPathRoot($path),[StringComparison]::OrdinalIgnoreCase)){throw 'Lifecycle references cannot be volume roots.'}
    if($path.Length -gt [IO.Path]::GetPathRoot($path).Length){$path=$path.TrimEnd('\')}
    $owner=$path
    $managed=$false
    foreach($root in $roots) {
        # A managed root itself is not one of its child workspaces. It still
        # takes precedence over a containing permanent source directory.
        if($path.TrimEnd('\').Equals($root,[StringComparison]::OrdinalIgnoreCase)) {
            $managed=$true
            break
        }
        $prefix=$root+'\'
        if($path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
            $relative=$path.Substring($prefix.Length)
            $separator=$relative.IndexOf('\')
            $child=if($separator -lt 0){$relative}else{$relative.Substring(0,$separator)}
            $owner=Join-Path $root $child
            $managed=$true
            break
        }
    }
    if(-not $managed) {
        foreach($root in $sourceRoots) {
            if($path.Equals($root,[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) {
                $owner=$root
                break
            }
        }
    }
    if($seen.Add($owner)){$owner}
}

function Select-StaticAnalysisAssemblies([string[]]$Paths, [version]$CompilerVersion) {
    # Match NuGet's compiler-specific asset selection: one newest compatible
    # roslynX.Y directory per package, never all variants at once.
    $selected=@{}
    foreach ($path in $Paths) {
        if ($path -match '^(?<root>.*[\\/]analyzers[\\/]dotnet)[\\/]roslyn(?<version>\d+\.\d+)[\\/]') {
            $root=$Matches.root; $version=[version]$Matches.version
            if ($version -le $CompilerVersion -and (-not $selected.ContainsKey($root) -or $version -gt $selected[$root])) { $selected[$root]=$version }
        }
    }
    foreach ($path in $Paths) {
        if ($path -match '^(?<root>.*[\\/]analyzers[\\/]dotnet)[\\/]roslyn(?<version>\d+\.\d+)[\\/]') {
            if (-not $selected.ContainsKey($Matches.root)) { throw "No compatible analyzer assets for $($Matches.root) and compiler $CompilerVersion" }
            if ([version]$Matches.version -ne $selected[$Matches.root]) { continue }
        }
        $path
    }
}

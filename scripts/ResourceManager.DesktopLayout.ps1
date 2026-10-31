Set-StrictMode -Version Latest

function Get-ResourceManagerDesktopLayout {
    # Structural identities have one declaration, shared with all managed consumers.
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/Shared/Packages/ProductDesktopLayout.cs'))
    $result = [ordered]@{}
    foreach ($name in @('DirectoryName', 'ResidentExecutableName', 'WindowExecutableName', 'BackendDirectoryName', 'BackendExecutableName', 'TaskManagerCommandMarker')) {
        $match = [regex]::Match($source, 'public const string ' + $name + ' = "([^"/\\]+)";')
        if (-not $match.Success) { throw "Missing product desktop layout identity: $name" }
        $result[$name] = $match.Groups[1].Value
    }
    [pscustomobject]$result
}

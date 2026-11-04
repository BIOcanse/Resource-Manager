#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$AssemblyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata

foreach ($path in $AssemblyPath) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $stream = [System.IO.File]::OpenRead($fullPath)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $pe.HasMetadata) { throw "Assembly has no managed metadata: $fullPath" }
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($handle in $metadata.TypeDefinitions) {
                $definition = $metadata.GetTypeDefinition($handle)
                if ($metadata.GetString($definition.Name) -ceq 'StateNames') {
                    throw "Release assembly contains a StateNames type: $fullPath"
                }
            }
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
    [pscustomobject]@{ AssemblyPath = $fullPath; StateNamesAbsent = $true }
}

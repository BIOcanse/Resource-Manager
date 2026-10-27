[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Add', 'Move', 'Resolve', 'Export')][string]$Action,
    [string]$Registry = (Join-Path $PSScriptRoot '../../src/Shared/StateItems/state-items.json'),
    [ValidateSet('block', 'item', 'condition')][string]$Kind,
    [ValidateSet('backend', 'ui', 'desktop', 'window')][string]$Process,
    [string]$Name,
    [string]$Comment,
    [ValidateSet('own', 'reference')][string]$Storage,
    [string]$Type,
    [long]$OwnerBlock,
    [string]$OwnerComponent,
    [bool]$PerBlock,
    [ValidateSet('event', 'poll')][string]$Trigger,
    [string]$ChangeEvent,
    [string]$PollInterval,
    [long]$Node,
    [System.Collections.IDictionary[]]$ItemReads = @(),
    [long[]]$Conditions = @(),
    [long[]]$Children = @(),
    [long]$Decides,
    [long]$Uid,
    [long[]]$Uids,
    [string]$Version,
    [string]$TableDirectory,
    [string]$Destination
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../build/StateItemRegistry.psm1') -Force

$registryPath = [IO.Path]::GetFullPath($Registry)
switch ($Action) {
    'Move' {
        if (-not $Process -or -not $Uids -or $Uids.Count -eq 0) { throw 'Move requires -Process and -Uids.' }
        $table = Read-StateRegistry -Path $registryPath
        $selected = [Collections.Generic.HashSet[long]]::new()
        foreach ($id in $Uids) {
            if ($id -lt 1 -or -not $selected.Add($id)) { throw 'Move requires distinct positive Uids.' }
            $entry = @($table.entries | Where-Object uid -EQ $id)
            if ($entry.Count -ne 1) { throw "Registry entry uid=$id was not found." }
            $entry[0].process = $Process
        }
        # Move owners and their publications together. Validate the complete result
        # before writing, so a failed move cannot leave cross-process references.
        Write-StateRegistry -Path $registryPath -Registry $table
    }
    'Add' {
        foreach ($required in @('Kind', 'Process', 'Name', 'Comment')) {
            if (-not $PSBoundParameters.ContainsKey($required)) { throw "Add requires -$required." }
        }
        $table = Read-StateRegistry -Path $registryPath
        $next = 1L
        foreach ($entry in $table.entries) {
            if ($entry.uid -ne $next) { break }
            $next++
        }
        $new = [ordered]@{ uid = $next; kind = $Kind; process = $Process; name = $Name; comment = $Comment }
        $itemParameters = @('Storage', 'Type', 'OwnerBlock', 'OwnerComponent', 'PerBlock', 'Trigger', 'ChangeEvent', 'PollInterval')
        $conditionParameters = @('Node', 'ItemReads', 'Conditions', 'Children', 'Decides')
        if ($Kind -ne 'condition') {
            foreach ($parameter in $conditionParameters) {
                if ($PSBoundParameters.ContainsKey($parameter)) { throw "Add $Kind cannot use -$parameter." }
            }
        }
        if ($Kind -eq 'item') {
            foreach ($required in @('Storage', 'Type', 'PerBlock', 'Trigger')) {
                if (-not $PSBoundParameters.ContainsKey($required)) { throw "Add item requires -$required." }
            }
            if ($PSBoundParameters.ContainsKey('OwnerBlock') -eq $PSBoundParameters.ContainsKey('OwnerComponent')) { throw 'Add item requires exactly one of -OwnerBlock or -OwnerComponent.' }
            $new.storage = $Storage
            $new.type = $Type
            $new.owner = if ($PSBoundParameters.ContainsKey('OwnerBlock')) { [ordered]@{ block = $OwnerBlock } } else { [ordered]@{ component = $OwnerComponent } }
            $new.perBlock = $PerBlock
            $new.trigger = $Trigger
            if ($PSBoundParameters.ContainsKey('ChangeEvent')) { $new.changeEvent = $ChangeEvent }
            if ($PSBoundParameters.ContainsKey('PollInterval')) { $new.pollInterval = $PollInterval }
        } elseif ($Kind -eq 'condition') {
            foreach ($parameter in $itemParameters | Where-Object { $_ -ne 'PerBlock' }) {
                if ($PSBoundParameters.ContainsKey($parameter)) { throw "Add condition cannot use -$parameter." }
            }
            if (-not $PSBoundParameters.ContainsKey('PerBlock')) { throw 'Add condition requires -PerBlock.' }
            $new.perBlock = $PerBlock
            if ($PSBoundParameters.ContainsKey('Node')) { $new.node = $Node }
            $new.items = @($ItemReads)
            $new.conditions = @($Conditions)
            $new.children = @($Children)
            if ($PSBoundParameters.ContainsKey('Decides')) { $new.decides = $Decides }
        } else {
            foreach ($parameter in $itemParameters) {
                if ($PSBoundParameters.ContainsKey($parameter)) { throw "Add block cannot use -$parameter." }
            }
        }
        $table.entries = @(@($table.entries) + @($new) | Sort-Object { $_['uid'] })
        Write-StateRegistry -Path $registryPath -Registry $table
        $next
    }
    'Resolve' {
        if (-not $PSBoundParameters.ContainsKey('Uid') -or $Uid -lt 1) { throw 'Resolve requires -Uid >= 1.' }
        if ($PSBoundParameters.ContainsKey('Version')) {
            if ($Version -cnotmatch '^[0-9a-f]{40}$') { throw 'Version must be a lowercase 40-character git blob object id.' }
            $published = if ($TableDirectory) { Join-Path $TableDirectory "$Version.json" } else { $null }
            if ($published -and [IO.File]::Exists($published)) { $bytes = [IO.File]::ReadAllBytes($published) } else {
                $start = [Diagnostics.ProcessStartInfo]::new('git')
                foreach ($argument in @('-C', [IO.Path]::GetDirectoryName($registryPath), 'cat-file', 'blob', $Version)) { $start.ArgumentList.Add($argument) }
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $git = [Diagnostics.Process]::Start($start)
                $buffer = [IO.MemoryStream]::new()
                try {
                    $errorRead = $git.StandardError.ReadToEndAsync()
                    $git.StandardOutput.BaseStream.CopyTo($buffer)
                    $git.WaitForExit()
                    if ($git.ExitCode -ne 0) { throw "Registry version $Version could not be found in the table directory or git: $($errorRead.GetAwaiter().GetResult())" }
                    $bytes = $buffer.ToArray()
                } finally { $buffer.Dispose(); $git.Dispose() }
            }
            if ((Get-StateRegistryVersion $bytes) -cne $Version) { throw "Registry version ${Version}: blob hash mismatch." }
            $table = Read-StateRegistry -Bytes $bytes -HistoricalTable
        } else { $table = Read-StateRegistry -Path $registryPath }
        $entry = @($table.entries | Where-Object uid -EQ $Uid)
        if ($entry.Count -ne 1) { throw "Registry entry uid=$Uid was not found." }
        ConvertTo-Json -InputObject $entry[0] -Depth 6
    }
    'Export' {
        if ([string]::IsNullOrWhiteSpace($Destination)) { throw 'Export requires -Destination.' }
        $bytes = [IO.File]::ReadAllBytes($registryPath)
        $null = Read-StateRegistry -Bytes $bytes
        $version = Get-StateRegistryVersion $bytes
        $path = Join-Path ([IO.Path]::GetFullPath($Destination)) "$version.json"
        if ([IO.File]::Exists($path)) {
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($bytes)) { throw "Export destination exists with different bytes: $path" }
        } else {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try { $stream.Write($bytes) } finally { $stream.Dispose() }
        }
        $path
    }
}

#Requires -Version 7
<#
Owner workflow (Prepare builds only; Run requires an effective administrator):
  ./scripts/validation/Invoke-ControlActualTimingCopy.ps1 -Prepare -RegisteredWorkspaceRoot <registered task> -BeforeReaderOverlay <original reader.cs>
  ./scripts/validation/Invoke-ControlActualTimingCopy.ps1 -Run -RegisteredWorkspaceRoot <same task>
Optional properties JSON can supplement the normal serial native/Ledger recipe.
Use -BeforeSourceRoot for an owner-bound baseline source tree, or -BeforeImage for an
already accepted image containing this mode. The script never claims an overlay is cd77be4c.
Lifecycle of RegisteredWorkspaceRoot belongs to its owner, which must register consumption
references before completing it. This script never completes the enclosing task.
#>
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][switch]$Prepare,
    [Parameter(Mandatory, ParameterSetName = 'Run')][switch]$Run,
    [Parameter(Mandatory)][string]$RegisteredWorkspaceRoot,
    [string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [Parameter(ParameterSetName = 'Prepare')][string]$BuildPropertiesFile,
    [Parameter(ParameterSetName = 'Prepare')][string]$BeforeSourceRoot,
    [Parameter(ParameterSetName = 'Prepare')][string]$BeforeReaderOverlay,
    [Parameter(ParameterSetName = 'Prepare')][string]$BeforeImage,
    # Defaults to a child of this task; existing -LedgerWorkRoot inputs remain explicit.
    [Parameter(ParameterSetName = 'Prepare')][string]$LedgerWorkRoot,
    # Defaults to <ToolsRoot>\JobQueue\JobQueue.ps1 (scripts/dev/MachineRoots.psm1).
    [string]$JobQueueScript,
    [string]$MemoryLimitScript = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dev/Invoke-WithMemoryLimit.ps1'),
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,60}$')][string]$Name = 'actual-timing'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $JobQueueScript -or ($Prepare -and -not $LedgerWorkRoot)) {
    Import-Module (Join-Path $PSScriptRoot '..\dev\MachineRoots.psm1') -DisableNameChecking
    if (-not $JobQueueScript) { $JobQueueScript = Join-Path (Get-MachineRoot -Name ToolsRoot) 'JobQueue\JobQueue.ps1' }
    if ($Prepare -and -not $LedgerWorkRoot) {
        $LedgerWorkRoot = Join-Path $RegisteredWorkspaceRoot 'ledger'
    }
}
$owner = [IO.Path]::GetFullPath($RegisteredWorkspaceRoot).TrimEnd('\')
if (-not [IO.File]::Exists((Join-Path $owner '.generated-workspace.json'))) { throw 'Supply an existing registered task; this tool does not close that task.' }
$ownerState = Get-Content -LiteralPath (Join-Path $owner '.generated-workspace.json') -Raw | ConvertFrom-Json
if ($ownerState.state -ne 'running' -or [IO.Path]::GetFullPath($ownerState.workspacePath).TrimEnd('\') -ine $owner) { throw 'The supplied owner must be a running registered task.' }
$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$planPath = Join-Path $owner "$Name-plan.json"
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 20), $utf8)
}
function File-Row([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Input must be an ordinary file: $Path" }
    [pscustomobject]@{ path = $item.FullName; length = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
}
function Check-Rows($Rows) {
    foreach ($row in $Rows) {
        $current = File-Row $row.path
        if ($current.length -ne $row.length -or $current.sha256 -ne $row.sha256) { throw "Input changed: $($row.path)" }
    }
}
function Bounded-Files([string]$Root, [bool]$SourceTree) {
    $queue = [Collections.Generic.Queue[object]]::new()
    $queue.Enqueue([pscustomobject]@{ path = $Root; depth = 0 })
    $count = 0
    while ($queue.Count -gt 0) {
        $entry = $queue.Dequeue()
        if (++$count -gt 60000 -or $entry.depth -gt 24) { throw 'File traversal exceeded bounds.' }
        if ([IO.File]::GetAttributes($entry.path) -band [IO.FileAttributes]::ReparsePoint) { throw "Links forbidden in selected inputs: $($entry.path)" }
        foreach ($path in [IO.Directory]::EnumerateFiles($entry.path)) {
            if (++$count -gt 60000) { throw 'File traversal exceeded bounds.' }
            if ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) { throw "File link forbidden: $path" }
            $path
        }
        foreach ($child in [IO.Directory]::EnumerateDirectories($entry.path)) {
            $leaf = [IO.Path]::GetFileName($child)
            $relative = [IO.Path]::GetRelativePath($Root, $child)
            if ($SourceTree -and ($leaf -in @('.git','.local','.agents','.codex','bin','obj','node_modules','.zig-cache','zig-out','restored-packages') -or
                $relative -match '^src[\\/]UI[\\/]Frontend([\\/]|$)' -or $relative -match '(^|[\\/])tests([\\/]|$)')) { continue }
            if ($SourceTree -and $entry.depth -eq 0 -and $leaf -notin @('src','scripts','sdk','artifacts')) { continue }
            # Retain the fixed SDK feeds/native inputs declared by NuGet.config/Core;
            # omit unrelated historical build output under artifacts.
            if ($SourceTree -and $entry.depth -eq 1 -and [IO.Path]::GetFileName($entry.path) -eq 'artifacts' -and
                $leaf -notin @('sdk','disk-sdk','fan-control-core')) { continue }
            $queue.Enqueue([pscustomobject]@{ path = $child; depth = $entry.depth + 1 })
        }
    }
}
function Copy-Checked([string]$From, [string]$To) {
    $before = File-Row $From
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($To))
    [IO.File]::Copy($From, $To, $false)
    $after = File-Row $To
    if ($before.length -ne $after.length -or $before.sha256 -ne $after.sha256) { throw "Copy changed: $From" }
    Check-Rows @($before)
    $after
}
function Prepare-Image([string]$Label, [string]$InputRoot, [string]$ReaderOverlay, $Properties) {
    $work = Join-Path $owner "$Name-$Label"
    if (Test-Path -LiteralPath $work) { throw "Use a fresh Name; output already exists: $work" }
    # All snapshot/build/image/runtime descendants belong to the already registered owner task.
    $snapshot = Join-Path $work 'source'
    $built = Join-Path $work 'built'
    $image = Join-Path $work 'image'
    $rows = [Collections.Generic.List[object]]::new()
    $origins = [Collections.Generic.List[object]]::new()
    $bytes = 0L
    foreach ($file in Bounded-Files $InputRoot $true) {
        $relative = [IO.Path]::GetRelativePath($InputRoot, $file)
        # Only repository build inputs, never a private data tree or arbitrary root output.
        if ($relative -match '[\\/]' -and $relative -notmatch '^(src|scripts|sdk|artifacts)[\\/]') { continue }
        if ($relative -notmatch '[\\/]' -and [IO.Path]::GetExtension($relative) -notin @('.props','.targets','.config','.json','.sln','.slnx','.md','.txt') -and $relative -notin @('LICENSE','NOTICE','.editorconfig')) { continue }
        $input = File-Row $file
        $bytes += $input.length
        if ($bytes -gt 2GB) { throw 'Source snapshot exceeds 2 GiB; use an owner-prepared bounded source root.' }
        $origins.Add($input)
        $rows.Add((Copy-Checked $file (Join-Path $snapshot $relative)))
    }
    $provenance = 'current-tree-snapshot'
    if ($Label -eq 'before' -and $BeforeSourceRoot) {
        # The owner supplies the exact source identity. This tool adds the dev-only harness
        # without changing baseline product sources and records the overlay explicitly.
        $provenance = 'owner-supplied-before-source-with-development-harness-overlay; owner must bind cd77be4c'
        $harness = 'src/Development/ControlDecisionAcceptance'
        foreach ($file in Bounded-Files (Join-Path $source $harness) $true) {
            $destination = Join-Path (Join-Path $snapshot $harness) ([IO.Path]::GetRelativePath((Join-Path $source $harness), $file))
            if (Test-Path -LiteralPath $destination) { [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($file)) }
            else { [void](Copy-Checked $file $destination) }
            $origins.Add((File-Row $file))
        }
    }
    if ($ReaderOverlay) {
        $provenance = 'before-reader-behavior-reproduction-on-current-tree; not exact cd77be4c'
        $overlay = [IO.File]::ReadAllText($ReaderOverlay)
        if ($overlay -notmatch 'StartupDelay\s*=\s*TimeSpan.FromSeconds\(12\)' -or $overlay -notmatch 'await catalog.ReadObjectsAsync') {
            throw 'Before reader overlay must be the reviewed original StartupDelay12s + ReadObjectsAsync reader.'
        }
        if ($overlay -notmatch 'AcquireSubscription\(TimeSpan interval\)') {
            $needle = 'public IDisposable AcquireSubscription()'
            if ($overlay.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) { throw 'Unrecognized before reader subscription signature.' }
            $overlay = $overlay.Replace($needle, "public IDisposable AcquireSubscription(TimeSpan interval) => AcquireSubscription();`n`n    $needle")
        }
        $path = Join-Path $snapshot 'src/Core/Infrastructure/Control/ControlActualStateReader.cs'
        [IO.File]::WriteAllText($path, $overlay, $utf8)
        $origins.Add((File-Row $ReaderOverlay))
    }
    # Re-enumerate after explicit overlays; never silently keep stale pre-overlay hashes.
    $rows.Clear()
    foreach ($file in Bounded-Files $snapshot $true) { $rows.Add((File-Row $file)) }
    Check-Rows $origins
    $inputs = Join-Path $work 'evaluated-inputs.txt'
    $propertiesPath = Join-Path $work 'build-properties.json'
    Write-Json $propertiesPath $Properties
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($value in @('build',(Join-Path $snapshot 'src/Development/ControlDecisionAcceptance/ResourceManager.ControlDecisionAcceptance.csproj'),
        '-c','Debug','--output',$built,'--disable-build-servers','-m:1','-nr:false','-p:BuildInParallel=false','-p:ResourceManagerStateNames=true',
        '-p:ResourceManagerZigJobs=1','-p:SkipClientAppBuild=true','-p:UseSharedCompilation=false','-p:NuGetAudit=false',"-p:ActualTimingInputsFile=$inputs")) { $arguments.Add($value) }
    foreach ($property in $Properties.PSObject.Properties) {
        if ($property.Name -notmatch '^[A-Za-z][A-Za-z0-9_]*$' -or [string]$property.Value -match '[\r\n;]') { throw 'Invalid MSBuild property.' }
        if ($property.Name -in @('ResourceManagerStateNames','ResourceManagerZigJobs','BuildInParallel','ActualTimingInputsFile','UseSharedCompilation','NuGetAudit','SkipClientAppBuild')) { throw "Do not override required timing build property $($property.Name)." }
        $arguments.Add("-p:$($property.Name)=$($property.Value)")
    }
    $queueResult = & $JobQueueScript -Action Run -FilePath $dotnet -ArgumentList $arguments.ToArray() -WorkingDirectory $snapshot -MemoryLimitGiB 4 -MemoryLimitScript $MemoryLimitScript -Label "control-actual-timing-$Label" -TimeoutMinutes 60
    $queueExit = $LASTEXITCODE
    $queueResult | Out-Host
    if ($queueExit -ne 0) { throw "Queued $Label build failed ($queueExit)." }
    Check-Rows $rows
    if (-not (Test-Path -LiteralPath $inputs)) { throw 'Build omitted evaluated compile/reference inputs.' }
    $evaluated = [Collections.Generic.List[object]]::new()
    foreach ($path in @([IO.File]::ReadAllLines($inputs) | Where-Object { $_ -and [IO.File]::Exists($_) } | Sort-Object -Unique)) {
        $evaluated.Add((File-Row $path))
    }
    # Core is a referenced project, so its generated compiler inputs do not appear
    # in this executable's @(Compile). Bind the actual generated C# files too,
    # separately from immutable source rows: these are build outputs, never checked
    # against pre-build content. Native outputs remain in the copied artifact list.
    $generated = [Collections.Generic.List[object]]::new()
    $ledgerConsumer = Join-Path ([string]$Properties.ResourceManagerLedgerWorkRoot) 'consumer'
    if ($Properties.PSObject.Properties['ResourceManagerLedgerBuildRoot'] -and $Properties.ResourceManagerLedgerBuildRoot) {
        $ledgerConsumer = [IO.Path]::GetFullPath([string]$Properties.ResourceManagerLedgerBuildRoot)
    }
    foreach ($generatedRoot in @((Join-Path $snapshot 'src/Core/obj'), $ledgerConsumer)) {
        if (-not [IO.Directory]::Exists($generatedRoot)) { continue }
        foreach ($file in Bounded-Files $generatedRoot $false) {
            if ([IO.Path]::GetExtension($file) -eq '.cs') { $generated.Add((File-Row $file)) }
        }
    }
    if ($evaluated.Count -lt 3 -or $evaluated.Count -gt 24000) { throw 'Invalid evaluated input manifest.' }
    $sourceManifest = Join-Path $work 'source-input-manifest.json'
    Write-Json $sourceManifest ([ordered]@{ contract = 'resource-manager-control-decision-source-inputs-v1'; generatedUtc = [DateTime]::UtcNow.ToString('o');
        provenance = $provenance; ownerBaselineRequirement = 'Before exact cd77be4c identity requires owner source/image binding; this script executes no git commands.';
        originalSourceRoot = $InputRoot; firstPartySources = @($rows.ToArray()) + @((File-Row $PSCommandPath)); originalInputs = $origins.ToArray();
        evaluatedCompileAndReferences = $evaluated.ToArray(); generatedCompilerInputs = $generated.ToArray(); buildProperties = $Properties })
    [void][IO.Directory]::CreateDirectory($image)
    $artifacts = [Collections.Generic.List[object]]::new()
    $total = 0L
    foreach ($file in Bounded-Files $built $false) {
        $relative = [IO.Path]::GetRelativePath($built, $file)
        if ([IO.Path]::GetExtension($file) -eq '.exe' -or $relative -match '^(Dependencies|GpuPlacementShim|wwwroot)[\\/]') { continue }
        $row = Copy-Checked $file (Join-Path $image $relative)
        $total += $row.length
        if ($total -gt 2GB -or $artifacts.Count -ge 10000) { throw 'Copied image exceeds bounds.' }
        $artifacts.Add([pscustomobject]@{ path = $relative; length = $row.length; sha256 = $row.sha256 })
    }
    [void](Copy-Checked $sourceManifest (Join-Path $image 'source-input-manifest.json'))
    Write-Json (Join-Path $image 'artifact-manifest.json') ([ordered]@{ contract = 'resource-manager-control-decision-copy-v1';
        sourceManifestSha256 = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash; files = $artifacts.ToArray() })
    [pscustomobject]@{ label = $Label; image = $image; provenance = $provenance; sourceManifest = (Join-Path $image 'source-input-manifest.json') }
}

if ($Prepare) {
    if (Test-Path -LiteralPath $planPath) { throw 'Plan already exists; use a new Name.' }
    if (@($BeforeSourceRoot,$BeforeReaderOverlay,$BeforeImage | Where-Object { $_ }).Count -ne 1) {
        throw 'Choose exactly one before input: -BeforeSourceRoot, -BeforeReaderOverlay, or -BeforeImage. Exact cd77be4c is owner-bound.'
    }
    $ledger = [IO.Path]::GetFullPath($LedgerWorkRoot).TrimEnd('\')
    if (-not [IO.File]::Exists((Join-Path $ledger '.generated-workspace.json'))) { throw 'LedgerWorkRoot must be an existing registered task.' }
    $ledgerState = Get-Content -LiteralPath (Join-Path $ledger '.generated-workspace.json') -Raw | ConvertFrom-Json
    if ($ledgerState.state -ne 'running') { throw 'LedgerWorkRoot must remain running until its owner completes all consumers.' }
    $properties = [pscustomobject]@{ ResourceManagerLedgerWorkRoot = $ledger }
    if ($BuildPropertiesFile) {
        $overrides = Get-Content -LiteralPath $BuildPropertiesFile -Raw | ConvertFrom-Json
        foreach ($property in $overrides.PSObject.Properties) {
            $properties | Add-Member -MemberType NoteProperty -Name $property.Name -Value $property.Value -Force
        }
    }
    $images = [Collections.Generic.List[object]]::new()
    if ($BeforeImage) {
        $beforePath = [IO.Path]::GetFullPath($BeforeImage).TrimEnd('\')
        if (-not $beforePath.StartsWith($owner + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'BeforeImage must be under the same registered owner task.' }
        [void](File-Row (Join-Path $beforePath 'artifact-manifest.json'))
        [void](File-Row (Join-Path $beforePath 'source-input-manifest.json'))
        $images.Add([pscustomobject]@{ label = 'before'; image = $beforePath; provenance = 'owner-supplied-before-image; owner must bind cd77be4c'; sourceManifest = (Join-Path $beforePath 'source-input-manifest.json') })
    }
    else {
        $beforeRoot = if ($BeforeSourceRoot) { [IO.Path]::GetFullPath($BeforeSourceRoot).TrimEnd('\') } else { $source }
        $images.Add((Prepare-Image 'before' $beforeRoot $BeforeReaderOverlay $properties))
    }
    $images.Add((Prepare-Image 'after' $source '' $properties))
    foreach ($entry in $images) {
        $entry | Add-Member -MemberType NoteProperty -Name sourceManifestBinding -Value (File-Row $entry.sourceManifest)
        $entry | Add-Member -MemberType NoteProperty -Name artifactManifestBinding -Value (File-Row (Join-Path $entry.image 'artifact-manifest.json'))
    }
    Write-Json $planPath ([ordered]@{ contract = 'resource-manager-control-actual-timing-plan-v1'; owner = $owner;
        invocationScript = File-Row $PSCommandPath;
        memoryLimitScript = File-Row $MemoryLimitScript; images = $images.ToArray();
        lifecycle = 'Owner retains this registered task until consuming evidence, then completes it with the maintenance entrypoint.' })
    Write-Output $planPath
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Owner must run -Run in its effective administrator context.' }
$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if ($plan.contract -ne 'resource-manager-control-actual-timing-plan-v1' -or $plan.owner -ne $owner) { throw 'Unexpected timing plan.' }
Check-Rows @($plan.invocationScript)
Check-Rows @($plan.memoryLimitScript)
if ([IO.Path]::GetFullPath($MemoryLimitScript) -ne $plan.memoryLimitScript.path) { throw 'Memory-limit helper differs from prepared plan.' }
$runs = [Collections.Generic.List[object]]::new()
foreach ($entry in $plan.images) {
    Check-Rows @($entry.sourceManifestBinding, $entry.artifactManifestBinding)
    $imagePath = [IO.Path]::GetFullPath($entry.image).TrimEnd('\')
    if (-not $imagePath.StartsWith($owner + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Image escaped registered owner.' }
    $runtime = Join-Path $owner "$Name-$($entry.label)-run-$([Guid]::NewGuid().ToString('N'))"
    $launchPath = Join-Path $owner "$Name-$($entry.label)-launch-$([Guid]::NewGuid().ToString('N')).json"
    Write-Json $launchPath ([ordered]@{ memoryLimitScript = $MemoryLimitScript; filePath = $dotnet;
        argumentList = @((Join-Path $imagePath 'ResourceManager.ControlDecisionAcceptance.dll'), '--actual-timing','--registered-root',$owner,
            '--runtime-root',$runtime,'--artifact-manifest',(Join-Path $imagePath 'artifact-manifest.json'),
            '--source-manifest',(Join-Path $imagePath 'source-input-manifest.json'),'--label',[string]$entry.label) })
    # A fresh PowerShell child joins the memory-limit Job. Arguments cross this
    # process boundary as JSON, so spaces/arrays remain exact and the owner stays outside the Job.
    $manifestLiteral = $launchPath.Replace("'", "''")
    $launchCommand = "`$ErrorActionPreference = 'Stop'; `$launch = Get-Content -LiteralPath '$manifestLiteral' -Raw | ConvertFrom-Json; & ([string]`$launch.memoryLimitScript) -LimitGiB 4 -FilePath ([string]`$launch.filePath) -ArgumentList ([string[]]`$launch.argumentList); exit `$LASTEXITCODE"
    & $pwsh -NoProfile -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launchCommand)))
    $exitCode = $LASTEXITCODE
    $receiptPath = Join-Path $runtime 'evidence/actual-timing.json'
    $receipt = if (Test-Path -LiteralPath $receiptPath) { Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } else { $null }
    $runs.Add([pscustomobject]@{ label = $entry.label; provenance = $entry.provenance; exitCode = $exitCode; receiptPath = $receiptPath; receipt = $receipt })
}
$passed = $runs.Count -eq 2 -and @($runs | Where-Object { $_.exitCode -ne 0 -or $null -eq $_.receipt -or $_.receipt.status -ne 'passed' }).Count -eq 0
$comparison = [ordered]@{ contract = 'resource-manager-control-actual-timing-comparison-v1'; status = $(if ($passed) { 'passed' } else { 'failed' });
    generatedUtc = [DateTime]::UtcNow.ToString('o'); runs = $runs.ToArray();
    attribution = 'See each product-logs.ndjson. No inferred stage durations; missing normal-path boundaries are explicitly not attributable.' }
Write-Json (Join-Path $owner "$Name-comparison.json") $comparison
$comparison | ConvertTo-Json -Depth 20
if (-not $passed) { exit 1 }

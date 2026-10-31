#Requires -Version 7
<#
Focused checks for scripts/maintenance/Test-PublicPrivacy.ps1 and its use in Port-ToPublic.ps1.
WorkRoot is a registered running workspace; the fixture repositories stay under it and leave with its lifecycle.
Every identifier below is read from this machine at run time, so this file holds none of them.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$check = Join-Path $repository 'scripts/maintenance/Test-PublicPrivacy.ps1'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$script:count = 0
function Assert-True([bool]$Value, [string]$Reason) {
    if (-not $Value) { throw "Assertion failed: $Reason" }
    $script:count++
}
function Invoke-FixtureGit([string]$Repo, [string[]]$Arguments) {
    $output = @(& git -C $Repo @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output
}
function New-FixtureRepo([string]$Path) {
    [void][IO.Directory]::CreateDirectory($Path)
    Invoke-FixtureGit $Path @('init', '-q', '-b', 'codex/development-mainline') | Out-Null
    Invoke-FixtureGit $Path @('config', 'user.name', 'Fixture Maintainer') | Out-Null
    Invoke-FixtureGit $Path @('config', 'user.email', ('maintainer' + '@' + 'fixture-mail.net')) | Out-Null
    Invoke-FixtureGit $Path @('config', 'core.autocrlf', 'false') | Out-Null
}
function Write-Lines([string]$Path, [string[]]$Lines) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Path))
    [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}
function New-ZipBytes([string]$EntryName, [byte[]]$Content) {
    $buffer = [IO.MemoryStream]::new()
    $zip = [IO.Compression.ZipArchive]::new($buffer, [IO.Compression.ZipArchiveMode]::Create, $true)
    $stream = $zip.CreateEntry($EntryName).Open()
    $stream.Write($Content, 0, $Content.Length)
    $stream.Dispose()
    $zip.Dispose()
    $buffer.ToArray()
}
function New-GzipBytes([byte[]]$Content) {
    $buffer = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($buffer, [IO.Compression.CompressionMode]::Compress, $true)
    $gzip.Write($Content, 0, $Content.Length)
    $gzip.Dispose()
    $buffer.ToArray()
}
function New-PortablePdbBytes([string]$DocumentName, [byte[]]$EmbeddedSource) {
    # One document whose name the PDB stores in parts, and its source embedded deflated, as the compiler writes them.
    $metadata = [Reflection.Metadata.Ecma335.MetadataBuilder]::new()
    $csharp = $metadata.GetOrAddGuid([guid]'3f5162f8-07c6-11d3-9053-00c04fa302a1')
    $document = $metadata.AddDocument($metadata.GetOrAddDocumentName($DocumentName), $metadata.GetOrAddGuid([guid]::Empty), $metadata.GetOrAddBlob([byte[]]@()), $csharp)
    $deflated = [IO.MemoryStream]::new()
    $deflate = [IO.Compression.DeflateStream]::new($deflated, [IO.Compression.CompressionMode]::Compress, $true)
    $deflate.Write($EmbeddedSource, 0, $EmbeddedSource.Length)
    $deflate.Dispose()
    $value = [BitConverter]::GetBytes([int]$EmbeddedSource.Length) + $deflated.ToArray()
    [void]$metadata.AddCustomDebugInformation([Reflection.Metadata.EntityHandle]$document, $metadata.GetOrAddGuid([guid]'0E8A571B-6926-466E-B4AD-8AB04611F5FE'), $metadata.GetOrAddBlob([byte[]]$value))
    $rowCounts = [Collections.Immutable.ImmutableArray]::Create[int]([int[]]::new([Reflection.Metadata.Ecma335.MetadataTokens]::TableCount))
    $builder = [Reflection.Metadata.Ecma335.PortablePdbBuilder]::new($metadata, $rowCounts, [Reflection.Metadata.MethodDefinitionHandle]::new(), $null)
    $blob = [Reflection.Metadata.BlobBuilder]::new()
    [void]$builder.Serialize($blob)
    , $blob.ToArray()
}
function Get-Hits([string]$Repo, [hashtable]$Options = @{}) { @(& $check -Repo $Repo @Options 3>$null) }
function Find-Hit([object[]]$Hits, [string]$Path, [string]$Line, [string]$Rule) {
    # The comma keeps an empty result an array, so .Count works under StrictMode.
    , @($Hits | Where-Object { $_.Path -ceq $Path -and [string]$_.Line -ceq $Line -and $_.Rule -ceq $Rule })
}
function Assert-NoValues([object[]]$Hits, [string[]]$Secrets, [string]$Where) {
    foreach ($hit in $Hits) {
        foreach ($property in $hit.PSObject.Properties) {
            foreach ($secret in $Secrets) {
                Assert-True (-not ([string]$property.Value).Contains($secret, [StringComparison]::OrdinalIgnoreCase)) "$Where hit $($hit.Path):$($hit.Line) carries a matched value in $($property.Name)"
            }
        }
    }
}

Add-Type -AssemblyName System.IO.Compression, System.Reflection.Metadata
$fixture = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ('public-privacy-fixture-' + [Guid]::NewGuid().ToString('N'))
$profilePath = $env:USERPROFILE
$userName = $env:USERNAME
$machineGuid = try { [string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid } catch { '' }
# The first group of this machine's NVIDIA GPU UUID and its board subsystem id split as 0000VVVV:0000DDDD, when it has them.
$gpuFirstGroup = try { @(& nvidia-smi -L 2>$null | ForEach-Object { [regex]::Matches($_, 'GPU-[0-9a-fA-F]{8}(?=-)') | ForEach-Object Value }) | Select-Object -First 1 } catch { $null }
$subsystem = try {
    @((Get-PnpDevice).InstanceId | ForEach-Object { [regex]::Match((@($_ -split '\\') + @(''))[1], 'SUBSYS_([0-9A-Fa-f]{8})').Groups[1].Value } |
        Where-Object { $_ -and -not $_.StartsWith('0000') }) | Select-Object -First 1
} catch { $null }
$splitSubsystem = if ($subsystem) { "0000$($subsystem.Substring(4, 4)):0000$($subsystem.Substring(0, 4))" } else { $null }
$foreignEmail = 'someone' + '@' + 'fixture-mail.net'
$token = 'ghp_' + ('A1' * 18)
$localRootPath = Join-Path $fixture 'tools\zig\zig.exe'
$posixRest = $localRootPath.Substring(3).Replace('\', '/')
$posixDrive = $localRootPath.Substring(0, 1).ToLowerInvariant()
$secrets = @($profilePath, $userName, $foreignEmail, $token) + @($machineGuid, $gpuFirstGroup, $splitSubsystem | Where-Object { $_ })

# Full mode: every file the working tree would publish.
$dev = Join-Path $fixture 'dev'
New-FixtureRepo $dev
Write-Lines (Join-Path $dev '.gitignore') @('/ignored/')
Write-Lines (Join-Path $dev 'leak.txt') @(
    'clean line',
    "cache = $profilePath\.nuget\packages",
    ('"path": "' + $profilePath.Replace('\', '\\') + '\\AppData"'),
    "tool = $localRootPath",
    "guid = $machineGuid",
    "contact = $foreignEmail",
    "token = $token",
    'invented = C:\Users\example\AppData\Local\Product\app.exe PCI\VEN_1002&DEV_164E&SUBSYS_12345678&REV_D8\4&1A2B3C4D&0&0041',
    ('feed = https://user' + '@' + 'github.com/owner/repo and support' + '@' + 'example.com')
)
Write-Lines (Join-Path $dev 'ignored/secret.txt') @("cache = $profilePath")
[IO.File]::WriteAllBytes((Join-Path $dev 'package.nupkg'), (New-ZipBytes 'lib/net10.0/Fixture.dll' ([Text.Encoding]::Unicode.GetBytes("pdb $profilePath\obj\Fixture.pdb"))))
[IO.File]::WriteAllBytes((Join-Path $dev 'recorded.json.gz'), (New-GzipBytes ([Text.Encoding]::UTF8.GetBytes("{""source"":""$($localRootPath.Replace('\', '\\'))""}"))))
$pdb = New-PortablePdbBytes (Join-Path $fixture 'src\Fixture.cs') ([Text.Encoding]::UTF8.GetBytes("// cache = $profilePath\.nuget`n"))
[IO.File]::WriteAllBytes((Join-Path $dev 'symbols.nupkg'), (New-ZipBytes 'lib/net10.0/Fixture.pdb' $pdb))
Write-Lines (Join-Path $dev 'sources.txt') @('sources = load@gpu.0.usage, clock@cpu.12.core')
Write-Lines (Join-Path $dev 'posix.txt') @("bash = /$posixDrive/$posixRest", "wsl = /mnt/$posixDrive/$posixRest", "url = https://example.com/$posixDrive/$posixRest")
Write-Lines (Join-Path $dev 'ids.txt') @(
    $(if ($gpuFirstGroup) { "truncated = $gpuFirstGroup (first group only)" } else { 'no NVIDIA GPU' }),
    $(if ($splitSubsystem) { "split = $splitSubsystem" } else { 'no board subsystem id' })
)
Invoke-FixtureGit $dev @('add', '-A') | Out-Null
Invoke-FixtureGit $dev @('commit', '-q', '-m', 'Base fixture') | Out-Null
$baseCommit = @(Invoke-FixtureGit $dev @('rev-parse', 'HEAD'))[0]

$full = Get-Hits $dev
Assert-True ((Find-Hit $full 'leak.txt' '2' 'profile').Count -eq 1) 'a profile path is a hit'
Assert-True ((Find-Hit $full 'leak.txt' '2' 'user-name').Count -ge 1) 'the user name inside a profile path is a hit'
Assert-True ((Find-Hit $full 'leak.txt' '2' 'profile')[0].Column -eq ('cache = '.Length + 1)) 'the hit column points at the match'
Assert-True ((Find-Hit $full 'leak.txt' '3' 'profile').Count -eq 1) 'a JSON-escaped profile path is a hit'
Assert-True ((Find-Hit $full 'leak.txt' '4' 'local-root').Count -eq 1) 'a path under the checkout parent is a local-root hit'
if ($machineGuid) { Assert-True ((Find-Hit $full 'leak.txt' '5' 'machine-id').Count -eq 1) 'the MachineGuid is a machine-id hit' }
Assert-True ((Find-Hit $full 'leak.txt' '6' 'email').Count -eq 1) 'a foreign e-mail address is a hit'
Assert-True ((Find-Hit $full 'leak.txt' '7' 'token').Count -eq 1) 'a GitHub token shape is a hit'
Assert-True (@($full | Where-Object { $_.Path -ceq 'leak.txt' -and [string]$_.Line -in @('1', '8', '9') }).Count -eq 0) 'invented paths, ids, URL user info and reserved domains are not hits'
Assert-True (@($full | Where-Object { $_.Path -like 'ignored/*' }).Count -eq 0) 'ignored files are not scanned'
Assert-True (@($full | Where-Object { $_.Path -ceq 'sources.txt' }).Count -eq 0) 'field@device identifiers with an all-digit label are not e-mail hits'
Assert-True ((Find-Hit $full 'package.nupkg!lib/net10.0/Fixture.dll' 'bin' 'profile').Count -eq 1) 'a UTF-16 profile path inside a package entry is a hit'
Assert-True ((Find-Hit $full 'recorded.json.gz!gunzip' 'bin' 'local-root').Count -eq 1) 'a local root inside a gzip stream is a hit'
Assert-True ((Find-Hit $full 'symbols.nupkg!lib/net10.0/Fixture.pdb!pdb-documents' '1' 'local-root').Count -eq 1) 'a PDB document name, stored in parts, is joined and checked'
Assert-True ((Find-Hit $full 'symbols.nupkg!lib/net10.0/Fixture.pdb!pdb-source/1' 'bin' 'profile').Count -eq 1) 'a deflated embedded PDB source is inflated and checked'
Assert-True ((Find-Hit $full 'posix.txt' '1' 'local-root').Count -eq 1 -and (Find-Hit $full 'posix.txt' '2' 'local-root').Count -eq 1) 'Git Bash and WSL spellings of a local root are hits'
Assert-True (@($full | Where-Object { $_.Path -ceq 'posix.txt' -and [string]$_.Line -ceq '3' }).Count -eq 0) 'a URL path is not a POSIX drive path'
if ($gpuFirstGroup) { Assert-True ((Find-Hit $full 'ids.txt' '1' 'machine-id').Count -eq 1) 'the first group of the GPU UUID alone is a machine-id hit' }
if ($splitSubsystem) { Assert-True ((Find-Hit $full 'ids.txt' '2' 'machine-id').Count -eq 1) 'the split subsystem id spelling is a machine-id hit' }
Assert-True (@($full | Where-Object Level -cne 'Block').Count -eq 0) 'without -IncludeReview every hit blocks'
Assert-NoValues $full $secrets 'full'

$review = Get-Hits $dev @{ IncludeReview = $true }
Assert-True ((Find-Hit $review 'leak.txt' '8' 'any-profile').Count -eq 1 -and (Find-Hit $review 'leak.txt' '8' 'any-profile')[0].Level -ceq 'Review') '-IncludeReview reports an invented profile path for review only'
$excluded = Get-Hits $dev @{ ExcludePath = @('leak.txt', '*.nupkg') }
Assert-True (@($excluded | Where-Object { $_.Path -ceq 'leak.txt' -or $_.Path -like 'package.nupkg*' }).Count -eq 0) '-ExcludePath removes excluded files'
Assert-True ((Find-Hit $excluded 'recorded.json.gz!gunzip' 'bin' 'local-root').Count -eq 1) '-ExcludePath keeps the other files'

$allowFile = Join-Path $fixture 'allow.json'
$emailLine = "contact = $foreignEmail"
$lineSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($emailLine)))
ConvertTo-Json -InputObject @([ordered]@{ path = 'leak.txt'; rule = 'email'; lineSha256 = $lineSha; reason = 'fixture' }) | Set-Content -LiteralPath $allowFile -Encoding utf8
$allowedHits = Get-Hits $dev @{ AllowListPath = $allowFile }
Assert-True ((Find-Hit $allowedHits 'leak.txt' '6' 'email').Count -eq 0 -and (Find-Hit $allowedHits 'leak.txt' '7' 'token').Count -eq 1) 'an allow-list entry allows exactly its line and rule'
$explicitEmails = Get-Hits $dev @{ AllowedEmails = @($foreignEmail) }
Assert-True ((Find-Hit $explicitEmails 'leak.txt' '6' 'email').Count -eq 0) '-AllowedEmails allows the given address'

# Range mode: only what the commits add, plus their paths, binaries and messages.
Write-Lines (Join-Path $dev 'range.txt') @('first clean line', 'second clean line', "cache = $profilePath")
Write-Lines (Join-Path $dev "notes-$userName.txt") @('clean')
[IO.File]::WriteAllBytes((Join-Path $dev 'added.nupkg'), (New-ZipBytes 'README.md' ([Text.Encoding]::UTF8.GetBytes("feed = $localRootPath"))))
Invoke-FixtureGit $dev @('add', '-A') | Out-Null
Invoke-FixtureGit $dev @('commit', '-q', '-m', "Add range fixture`n`nWritten by $userName") | Out-Null
$rangeCommit = @(Invoke-FixtureGit $dev @('rev-parse', 'HEAD'))[0]
$range = Get-Hits $dev @{ Range = "$baseCommit..$rangeCommit" }
Assert-True ((Find-Hit $range 'range.txt' '3' 'profile').Count -eq 1) 'an added line is reported with its line number'
Assert-True (@($range | Where-Object { $_.Path -ceq 'leak.txt' }).Count -eq 0) 'lines outside the range are not reported'
Assert-True ((Find-Hit $range 'notes-<user-name>.txt' 'path' 'user-name').Count -eq 1) 'an added path is checked and printed with the identifier hidden'
Assert-True ((Find-Hit $range 'added.nupkg!README.md' 'bin' 'local-root').Count -eq 1) 'an added package is opened'
Assert-True ((Find-Hit $range "commit $($rangeCommit.Substring(0, 12))" '3' 'user-name').Count -eq 1) 'a commit message line is checked'
Assert-True (@($range | Where-Object { [string]$_.Line -in @('author', 'committer') }).Count -eq 0) 'the configured git identity is allowed'
Assert-NoValues $range $secrets 'range'

# Base mode: the working tree against a revision, untracked files included.
[IO.File]::AppendAllText((Join-Path $dev 'range.txt'), "token = $token`n", [Text.UTF8Encoding]::new($false))
Write-Lines (Join-Path $dev 'untracked/new.txt') @('clean', "contact = $foreignEmail")
$working = Get-Hits $dev @{ Base = 'HEAD' }
Assert-True ((Find-Hit $working 'range.txt' '4' 'token').Count -eq 1) 'a line added to a tracked file is reported'
Assert-True ((Find-Hit $working 'untracked/new.txt' '2' 'email').Count -eq 1) 'an untracked file is reported with its line number'
Assert-True (@($working | Where-Object { $_.Path -ceq 'range.txt' -and [string]$_.Line -ceq '3' }).Count -eq 0) 'committed lines are not reported again'
Assert-NoValues $working $secrets 'base'

# The port: a clean commit ports, a leaking one is refused with file:line [kind] and the public branch is reset.
$portDev = Join-Path $fixture 'port-dev'
$portPublic = Join-Path $fixture 'port-public'
New-FixtureRepo $portDev
foreach ($relative in @('scripts/maintenance/Port-ToPublic.ps1', 'scripts/maintenance/Test-PublicPrivacy.ps1', 'scripts/dev/MachineRoots.psm1')) {
    $target = Join-Path $portDev $relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
    [IO.File]::Copy((Join-Path $repository $relative), $target)
}
Write-Lines (Join-Path $portDev 'README.md') @('fixture')
# Already public before this fix; a later commit deletes the line.
Write-Lines (Join-Path $portDev 'legacy.txt') @('kept', "cache = $profilePath\.nuget\packages")
Invoke-FixtureGit $portDev @('add', '-A') | Out-Null
Invoke-FixtureGit $portDev @('commit', '-q', '-m', 'Base') | Out-Null
$portBase = @(Invoke-FixtureGit $portDev @('rev-parse', 'HEAD'))[0]
Invoke-FixtureGit $fixture @('clone', '-q', $portDev, $portPublic) | Out-Null
Invoke-FixtureGit $portPublic @('switch', '-q', '-c', 'main') | Out-Null
Invoke-FixtureGit $portPublic @('config', 'user.name', 'Fixture Maintainer') | Out-Null
Invoke-FixtureGit $portPublic @('config', 'user.email', ('maintainer' + '@' + 'fixture-mail.net')) | Out-Null
# Dev keeps files that are never ported, as the real dev checkout does.
Write-Lines (Join-Path $portDev 'dev-only/notes.txt') @('dev only')
Invoke-FixtureGit $portDev @('add', '-A') | Out-Null
Invoke-FixtureGit $portDev @('commit', '-q', '-m', 'Dev-only notes') | Out-Null
$portBase = @(Invoke-FixtureGit $portDev @('rev-parse', 'HEAD'))[0]
$portScript = Join-Path $portDev 'scripts/maintenance/Port-ToPublic.ps1'
# The port reads both machine roots; invented ones under the fixture serve here.
$env:RM_WORK_ROOT = Join-Path $fixture 'machine-work-root'
$env:RM_TOOLS_ROOT = Join-Path $fixture 'machine-tools-root'
$emptyGitConfig = Join-Path $fixture 'empty.gitconfig'
[IO.File]::WriteAllText($emptyGitConfig, '')
function Invoke-Port([string]$Range, [switch]$WithoutMachineRoots) {
    Invoke-FixtureGit $portPublic @('fetch', '-q', $portDev, 'codex/development-mainline:codex/development-mainline') | Out-Null
    # A separate process runs the port as the maintainer does; the refusal comes back as its exception message.
    $output = @(& $pwsh -NoProfile -NonInteractive -Command {
        param($Script, $PortRange, $Public, $Unconfigured, $EmptyConfig)
        # The mode is a word: a [bool] $false reached the child as a true value.
        if ($Unconfigured -eq 'unconfigured') {
            # Neither the environment nor any git configuration names a machine root.
            Remove-Item Env:RM_WORK_ROOT, Env:RM_TOOLS_ROOT -ErrorAction SilentlyContinue
            $env:GIT_CONFIG_GLOBAL = $EmptyConfig
            $env:GIT_CONFIG_NOSYSTEM = '1'
        }
        try { & $Script -Range $PortRange -Public $Public -NoPush 2>&1 | ForEach-Object { [string]$_ } }
        catch { 'Refused: ' + $_.Exception.Message; exit 1 }
    } -args $portScript, $Range, $portPublic, $(if ($WithoutMachineRoots) { 'unconfigured' } else { 'configured' }), $emptyGitConfig)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = (@($output | ForEach-Object { [string]$_ }) -join "`n") }
}
Write-Lines (Join-Path $portDev 'src/clean.txt') @('clean')
Invoke-FixtureGit $portDev @('add', '-A') | Out-Null
Invoke-FixtureGit $portDev @('commit', '-q', '-m', 'Clean change') | Out-Null
$portClean = @(Invoke-FixtureGit $portDev @('rev-parse', 'HEAD'))[0]
$publicStart = @(Invoke-FixtureGit $portPublic @('rev-parse', 'HEAD'))[0]
$unconfiguredPort = Invoke-Port "$portBase..$portClean" -WithoutMachineRoots
Assert-True ($unconfiguredPort.ExitCode -ne 0 -and $unconfiguredPort.Text.Contains('RM_WORK_ROOT')) "a port without configured machine roots stops and names the setting: $($unconfiguredPort.Text)"
Assert-True (@(Invoke-FixtureGit $portPublic @('rev-parse', 'HEAD'))[0] -ceq $publicStart) 'a port without machine roots changes nothing'
$cleanPort = Invoke-Port "$portBase..$portClean"
Assert-True ($cleanPort.ExitCode -eq 0 -and $cleanPort.Text -match 'ported 1 commit') "a clean commit ports: $($cleanPort.Text)"
$publicBefore = @(Invoke-FixtureGit $portPublic @('rev-parse', 'HEAD'))[0]
Write-Lines (Join-Path $portDev 'src/leak.txt') @("cache = $profilePath\.nuget\packages")
Invoke-FixtureGit $portDev @('add', '-A') | Out-Null
Invoke-FixtureGit $portDev @('commit', '-q', '-m', "Leak`n`nby $userName") | Out-Null
$portLeak = @(Invoke-FixtureGit $portDev @('rev-parse', 'HEAD'))[0]
$leakPort = Invoke-Port "$portClean..$portLeak"
Assert-True ($leakPort.ExitCode -ne 0 -and $leakPort.Text -match 'Port stopped') 'a leaking commit stops the port'
Assert-True ($leakPort.Text.Contains('src/leak.txt:1 [profile]')) 'the refusal lists file:line [kind]'
Assert-True ($leakPort.Text -match 'commit [0-9a-f]{12}:3 \[user-name\]') 'the refusal lists the commit message line'
foreach ($secret in @($profilePath, $userName)) { Assert-True (-not $leakPort.Text.Contains($secret, [StringComparison]::OrdinalIgnoreCase)) 'the refusal never prints the matched text' }
Assert-True (@(Invoke-FixtureGit $portPublic @('rev-parse', 'HEAD'))[0] -ceq $publicBefore) 'the refused port resets the public branch'
# Removing a leaking line is the fix itself: its deleted line is not checked, so it ports.
Write-Lines (Join-Path $portDev 'legacy.txt') @('kept')
Invoke-FixtureGit $portDev @('add', '-A') | Out-Null
Invoke-FixtureGit $portDev @('commit', '-q', '-m', 'Remove the local cache path') | Out-Null
$portFix = @(Invoke-FixtureGit $portDev @('rev-parse', 'HEAD'))[0]
$fixPort = Invoke-Port "$portLeak..$portFix"
Assert-True ($fixPort.ExitCode -eq 0 -and $fixPort.Text -match 'ported 1 commit') "a commit that only deletes a leaking line ports: $($fixPort.Text)"
Assert-True (-not (Get-Content -LiteralPath (Join-Path $portPublic 'legacy.txt') -Raw).Contains($profilePath, [StringComparison]::OrdinalIgnoreCase)) 'the ported deletion removed the line in public'

"PublicPrivacy tests passed: $script:count assertions"

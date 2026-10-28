#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [switch]$TestDiscovery,
    [string]$GeneratedJson,
    [string]$RetentionPolicyPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $repo 'scripts/tools/Export-ControlTree.ps1'
$script:count=0
function Assert-True([bool]$Value,[string]$Reason) {
    if (-not $Value) { throw "Assertion failed: $Reason" }
    $script:count++
}
function Assert-Rejected([scriptblock]$Operation,[string]$Pattern) {
    $caught=$null
    try { & $Operation | Out-Null } catch { $caught=$_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught -match $Pattern) "Expected /$Pattern/, got $caught"
}
function New-Declaration([string]$Profile,[string]$Launch=$null) {
    @{ profile=$Profile; inputs=@(); decidingCondition=@{uid=20;name='Wanted';perBlock=$true;items=@();conditions=@();children=@()}; launchesProcess=$(if ($Launch) {$Launch} else {$null}) }
}
function New-Block([int]$Uid,[string]$Name,$Parent,[object[]]$Declarations) {
    @{uid=$Uid;name=$Name;parent=$Parent;children=@();profiles=@($Declarations.profile);declarations=$Declarations}
}
function Write-Fixtures {
    $script:desktop=@{schemaVersion=1;process='desktop';registryVersion='fixture';program=1;profiles=@('normal','minimal');conditions=@();blocks=@(
        (New-Block 1 'Program' $null @((New-Declaration 'normal'),(New-Declaration 'minimal'))),
        (New-Block 2 'Frontend' 1 @((New-Declaration 'normal'),(New-Declaration 'minimal'))),
        (New-Block 3 'WindowProcess' 2 @((New-Declaration 'normal' 'window'))),
        (New-Block 4 'Tray' 2 @((New-Declaration 'normal'),(New-Declaration 'minimal')))
    )}
    $script:window=@{schemaVersion=1;process='window';registryVersion='fixture';program=5;profiles=@('normal','minimal');conditions=@();blocks=@(
        (New-Block 5 'Program' $null @((New-Declaration 'normal'),(New-Declaration 'minimal'))),
        (New-Block 6 'MainWindow' 5 @((New-Declaration 'normal'),(New-Declaration 'minimal'))),
        (New-Block 7 'WebViewHost' 6 @((New-Declaration 'normal'),(New-Declaration 'minimal')))
    )}
    Save-Fixtures
}
function Save-Fixtures {
    $desktop | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $desktopPath -Encoding utf8
    $window | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $windowPath -Encoding utf8
}
$batch='script-tests-'+[guid]::NewGuid().ToString('N')
$outcome='failed'
$prepared=$false
try {
    $workset=& (Join-Path $repo 'scripts/validation/Manage-ValidationWorkset.ps1') -Mode Prepare -WorkspaceRoot $WorkRoot -BatchId $batch -ConsumerId script -ExpectedGrowthGiB 1 -ReserveFreeGiB 2 -MaxWorksetGiB 8 -RetentionPolicyPath $RetentionPolicyPath | ConvertFrom-Json
    $prepared=$true
    $desktopPath=Join-Path $workset.IsolatedScratchRoot 'desktop.json'
    $windowPath=Join-Path $workset.IsolatedScratchRoot 'window.json'
    $files=@($desktopPath,$windowPath)
    Write-Fixtures
    $tree=& $tool -TreeJson $files -Format json | ConvertFrom-Json -Depth 100
    Assert-True ($tree.blocks.Count -eq 6) 'two processes stitched without program duplicates'
    $main=@($tree.blocks | Where-Object id -CEQ 'window:MainWindow')[0]
    Assert-True ($main.parent -ceq 'desktop:WindowProcess') 'launched process under lifetime owner'
    $sub=& $tool -TreeJson $files -From window:MainWindow -Format json | ConvertFrom-Json -Depth 100
    Assert-True ($sub.blocks.Count -eq 2 -and $null -eq $sub.blocks[0].parent) 'From inside launched process'
    $byUid=& $tool -TreeJson $files -From window:6 -Format json | ConvertFrom-Json -Depth 100
    Assert-True ($byUid.from -ceq 'window:MainWindow') 'UID selection'
    $program=& $tool -TreeJson $files -From desktop:1 -Format json | ConvertFrom-Json -Depth 100
    Assert-True ($program.from -ceq 'Program' -and $program.blocks.Count -eq 6) 'process program UID denotes software root'
    $byName=& $tool -TreeJson $files -From WebViewHost -Format json | ConvertFrom-Json -Depth 100
    Assert-True ($byName.blocks.Count -eq 1) 'globally unique name selection'
    $minimal=& $tool -TreeJson $files -Profile minimal -Format json | ConvertFrom-Json -Depth 100
    Assert-True ($minimal.blocks.Count -eq 3 -and @($minimal.blocks | Where-Object process -CEQ window).Count -eq 0) 'profile removes launcher and launched branch'
    $text=@(& $tool -TreeJson $files)
    $expected=@('Program','└─ desktop:Frontend  Wanted','   ├─ desktop:WindowProcess  Wanted','   │  └─ window:MainWindow  Wanted','   │     └─ window:WebViewHost  Wanted','   └─ desktop:Tray  Wanted')
    Assert-True (($text -join "`n") -ceq ($expected -join "`n")) 'exact three-column text and condition comments'
    $target=Join-Path $workset.IsolatedScratchRoot 'target.txt'
    $text | Set-Content -LiteralPath $target -Encoding utf8
    $diff=& $tool -TreeJson $files -CompareWith $target -Format json | ConvertFrom-Json
    Assert-True ($diff.missingInCode.Count -eq 0 -and $diff.extraInCode.Count -eq 0 -and $diff.parentDiffers.Count -eq 0) 'text comparison round trip'
    $markdown=Join-Path $workset.IsolatedScratchRoot 'target.md'
    @('# Target','Prose outside the tree.','```control-tree') + $text + @('```','After the tree.') | Set-Content -LiteralPath $markdown -Encoding utf8
    $diff=& $tool -TreeJson $files -CompareWith $markdown -Format json | ConvertFrom-Json
    Assert-True ($diff.missingInCode.Count -eq 0 -and $diff.extraInCode.Count -eq 0 -and $diff.parentDiffers.Count -eq 0) 'Markdown control-tree extraction'
    @('Program','└─ desktop:Frontend  arbitrary comment','   ├─ desktop:Missing','   ├─ window:MainWindow','   │  └─ window:WebViewHost','   └─ desktop:WindowProcess') | Set-Content -LiteralPath $target -Encoding utf8
    $diff=& $tool -TreeJson $files -CompareWith $target -Format json | ConvertFrom-Json
    Assert-True ($diff.missingInCode.Count -eq 1 -and $diff.missingInCode[0] -ceq 'desktop:Missing') 'target-only block'
    Assert-True ($diff.extraInCode.Count -eq 1 -and $diff.extraInCode[0] -ceq 'desktop:Tray') 'code-only block'
    Assert-True ($diff.parentDiffers.Count -eq 1 -and $diff.parentDiffers[0].block -ceq 'window:MainWindow' -and $diff.parentDiffers[0].targetParent -ceq 'desktop:Frontend' -and $diff.parentDiffers[0].codeParent -ceq 'desktop:WindowProcess') 'moved block and parents'
    $desktop.blocks[3].declarations[0].launchesProcess='window'; Save-Fixtures
    Assert-Rejected { & $tool -TreeJson $files } 'launched twice.*WindowProcess.*Tray'
    Write-Fixtures
    $desktop.blocks[2].declarations[0].launchesProcess='unknown'; Save-Fixtures
    Assert-Rejected { & $tool -TreeJson $files } 'unknown.*desktop:WindowProcess'
    Write-Fixtures
    $window.blocks[2].declarations[0].launchesProcess='desktop'; Save-Fixtures
    Assert-Rejected { & $tool -TreeJson $files } 'cycle.*desktop|cycle.*window'
    Write-Fixtures
    $window.blocks[2].name='Tray'; Save-Fixtures
    Assert-Rejected { & $tool -TreeJson $files -From Tray } 'exactly one.*2'
    Assert-Rejected { & $tool -TreeJson $files -Profile absent } 'Unknown profile'
    Assert-Rejected { & $tool -TreeJson $files -From nowhere } 'exactly one.*0'
    Write-Fixtures
    $window.blocks[1].parent=999; Save-Fixtures
    Assert-Rejected { & $tool -TreeJson $files } 'Unknown parent.*window:999.*MainWindow'
    Write-Fixtures
    # The launcher can remain in a profile without launching that process.
    $desktop.blocks[2].declarations+=New-Declaration 'minimal'; Save-Fixtures
    $minimal=& $tool -TreeJson $files -Profile minimal -Format json | ConvertFrom-Json -Depth 100
    Assert-True (@($minimal.blocks | Where-Object process -CEQ window).Count -eq 0) 'profile-specific launch metadata'
    if ($GeneratedJson) {
        $generated=& $tool -TreeJson $GeneratedJson -Format json | ConvertFrom-Json -Depth 100
        Assert-True ($generated.blocks.Count -eq 2 -and $generated.blocks[1].id -ceq 'fixture:Worker' -and $generated.blocks[1].declarations[0].decidingCondition.name -ceq 'Wanted') 'real Zig stdout consumed by script'
        Assert-True ($generated.processes[0].programBlock.name -ceq 'Program') 'process program metadata preserved'
    }
    if ($TestDiscovery) {
        $diagnostic=Join-Path $workset.IsolatedScratchRoot 'discovery.stderr'
        $output=@(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $tool -WorkRoot $WorkRoot 2> $diagnostic)
        $exitCode=$LASTEXITCODE
        $message=Get-Content -LiteralPath $diagnostic -Raw
        Assert-True ($exitCode -eq 0) "backend discovery succeeds: $message"
        Assert-True ($message -notmatch 'backend: no Zig declaration yet' -and $message -match 'desktop: no Zig declaration yet') 'backend export exists; desktop export remains absent'
        Assert-True (($output -join "`n") -match 'backend:Backend') 'software root includes backend tree'
    }
    $outcome='success'
    "Control tree export: $script:count assertions passed."
} finally {
    if ($prepared) { & (Join-Path $repo 'scripts/validation/Manage-ValidationWorkset.ps1') -Mode Complete -WorkspaceRoot $WorkRoot -BatchId $batch -Outcome $outcome -RetentionPolicyPath $RetentionPolicyPath | Out-Null }
}

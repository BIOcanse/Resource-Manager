[CmdletBinding()]
param(
    # 版本号只用于命名与发布说明，仓库里没有版本号字段。
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputRoot,
    [switch]$SkipPublish,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$softwareRoot = Join-Path $repositoryRoot 'Resource Manager'
$imageRoot = Join-Path $softwareRoot 'Bin\ResourceManagerFinal'
$publishScript = Join-Path $softwareRoot 'publish-all-win-x64.ps1'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\release'
}

function Write-Step([string]$Message) {
    if (-not $Quiet) { Write-Host "[release] $Message" }
}

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkingDirectory) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru -Wait
    try {
        if ($process.ExitCode -ne 0) {
            throw "$File 退出码 $($process.ExitCode)。"
        }
    }
    finally { $process.Dispose() }
}

if (-not $SkipPublish) {
    Write-Step '构建最终映像（publish-all-win-x64.ps1 -Target All）。'
    # build.cmd 依赖从当前目录解析可执行文件；某些 shell 里带着这个变量会让发布失败。
    $previous = $env:NoDefaultCurrentDirectoryInExePath
    Remove-Item Env:\NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue
    try {
        # 路径里有空格（Resource Manager），-File 后面必须自己带引号，
        # 否则 powershell.exe 把它拆成两个参数，报「文件没有 .ps1 扩展名」。
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$publishScript`"",
            '-Target', 'All') $repositoryRoot
    }
    finally {
        if ($null -ne $previous) { $env:NoDefaultCurrentDirectoryInExePath = $previous }
    }
}

if (-not (Test-Path -LiteralPath $imageRoot)) {
    throw "最终映像不存在：$imageRoot（先跑一次发布，或去掉 -SkipPublish）。"
}

$stage = Join-Path $OutputRoot "ResourceManager-$Version-win-x64"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# 发布包的构成：顶层入口脚本与说明 + scripts/（安装与启动）+ Bin/（最终映像）。
# Config/、UserData/、Dependencies/、Misc/ 都是运行时生成的，不进包。
$topLevel = @(
    'Install.cmd', 'LICENSE', 'NOTICE',
    'README.md', 'README.zh-CN.md',
    'Restart.cmd', 'Start.cmd')
$installScripts = @(
    'Register-ResourceManager.ps1',
    'ResourceManager.DirectoryRegistration.ps1',
    'Start-ResourceManagerInstalled.ps1')

Write-Step '收集顶层文件与安装脚本。'
foreach ($name in $topLevel) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $stage $name)
}
New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') -Force | Out-Null
foreach ($name in $installScripts) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts\$name") `
        -Destination (Join-Path $stage "scripts\$name")
}

Write-Step '复制最终映像。'
Copy-Item -LiteralPath $imageRoot -Destination (Join-Path $stage 'Bin') -Recurse -Force

Write-Step '生成 release-manifest.json。'
Push-Location $repositoryRoot
try {
    $sourceCommit = (& git rev-parse HEAD).Trim()
    $dirty = (& git status --porcelain) | Where-Object { $_ }
}
finally { Pop-Location }
if ($dirty) {
    Write-Warning '工作区有未提交改动：release-manifest.json 里记录的 sourceCommit 不能完整代表这个包。'
}

$files = @()
Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $files += [pscustomobject]@{
        path   = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        length = $_.Length
    }
}
[pscustomobject]@{ sourceCommit = $sourceCommit; files = $files } |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8

Write-Step '打包 zip 与校验和。'
$zipPath = Join-Path $OutputRoot "ResourceManager-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$zipPath.sha256"
"$zipHash  ResourceManager-$Version-win-x64.zip" | Set-Content -LiteralPath $hashPath -Encoding ascii

Write-Step '完成。'
[pscustomobject]@{
    Version      = $Version
    SourceCommit = $sourceCommit
    WorkingTree  = if ($dirty) { 'dirty' } else { 'clean' }
    FileCount    = $files.Count
    ZipPath      = $zipPath
    ZipBytes     = (Get-Item -LiteralPath $zipPath).Length
    ZipSha256    = $zipHash
    HashPath     = $hashPath
}

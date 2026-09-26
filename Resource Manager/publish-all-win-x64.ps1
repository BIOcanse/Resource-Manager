param(
    [ValidateSet("All", "Backend", "NativeUi", "Launcher")]
    [string]$Target = "All",
    [switch]$SkipFrontend,
    [switch]$SkipStop,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$SoftwareRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepositoryRoot = Split-Path -Parent $SoftwareRoot
$AppRoot = Join-Path $SoftwareRoot "Resource Manager-APP"
$ClientRoot = Join-Path $RepositoryRoot "src\UI\Frontend"
$BackendProject = Join-Path $AppRoot "ResourceManager.App.csproj"
$NativeUiProject = Join-Path $RepositoryRoot "src\UI\Core\ResourceManager.NativeUi.csproj"
$LauncherProject = Join-Path $AppRoot "Launcher\ResourceManager.Launcher.csproj"
$FrontendManifestPath = Join-Path $AppRoot "wwwroot\frontend-build.json"
$GpuPlacementShimRoot = Join-Path $AppRoot "Native\GpuPlacementShim"
$GpuPlacementShimBuild = Join-Path $GpuPlacementShimRoot "build.cmd"
$GpuPlacementProvider = Join-Path $GpuPlacementShimRoot "bin\win-x64\ResourceManager.GpuPlacementShim.dll"
$GpuPlacementBootstrap = Join-Path $GpuPlacementShimRoot "bin\win-x64\ResourceManager.GpuPlacementBootstrap.dll"
$GpuLaunchBrokerRoot = Join-Path $AppRoot "Native\GpuLaunchBroker"
$GpuLaunchBrokerBuild = Join-Path $GpuLaunchBrokerRoot "build.cmd"
$GpuLaunchBroker = Join-Path $GpuLaunchBrokerRoot "bin\win-x64\ResourceManager.GpuLaunchBroker.exe"
$BinRoot = Join-Path $SoftwareRoot "Bin"
$BackendOutput = Join-Path $BinRoot "ResourceManager"
$NativeUiOutput = Join-Path $BinRoot "ResourceManagerNativeUi"
$LauncherOutput = Join-Path $BinRoot "ResourceManagerLauncher"
$FinalOutput = Join-Path $BinRoot "ResourceManagerFinal"
$BackendExe = Join-Path $BackendOutput "ResourceManager.exe"
$NativeUiExe = Join-Path $NativeUiOutput "ResourceManager.NativeUi.exe"
$DevelopmentRunner = Join-Path $RepositoryRoot "scripts\Run-Development.ps1"
$FinalImageBuilder = Join-Path $RepositoryRoot "scripts\New-ResourceManagerFinalImage.ps1"
$FinalImageValidator = Join-Path $RepositoryRoot "scripts\Test-ResourceManagerFinalImage.ps1"
$ExecutableManifestValidator = Join-Path $RepositoryRoot "scripts\Test-WindowsExecutableManifest.ps1"

function Write-Step([string]$Message) {
    if (-not $Quiet) {
        Write-Host "[Resource Manager] $Message"
    }
}

function Assert-PathUnderBin([string]$Path) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $binResolved = [System.IO.Path]::GetFullPath($BinRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($binResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理 Bin 目录以外的发布路径：$resolved"
    }
}

function Clear-PublishDirectory([string]$Path) {
    Assert-PathUnderBin $Path
    if (Test-Path -LiteralPath $Path) {
        Write-Step "清理旧发布目录：$Path"
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-CheckedCommand([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Step "$FileName $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FileName @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FileName 退出码 $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-FrontendBuildIdentity([string]$ManifestPath) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "前端构建清单不存在：$ManifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "前端构建清单无法解析：$ManifestPath；$($_.Exception.Message)"
    }
    if ($manifest.contract -ne "resource-manager-frontend-assets-v1" -or
        [string]::IsNullOrWhiteSpace([string]$manifest.buildIdentity) -or
        @($manifest.entryScripts).Count -eq 0) {
        throw "前端构建清单合同无效：$ManifestPath"
    }

    return [string]$manifest.buildIdentity
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Clear-GpuLaunchInterceptionsBeforeBrokerReplacement {
    $arguments = @{
        FilePath = $GpuLaunchBroker
        ArgumentList = "--remove-all-resource-manager-ifeo"
        WindowStyle = "Hidden"
        Wait = $true
        PassThru = $true
    }
    if (-not (Test-Administrator)) {
        $arguments.Verb = "RunAs"
    }

    Write-Step "替换启动代理前清理本产品拥有的固定启动拦截。"
    $process = Start-Process @arguments
    if ($process.ExitCode -ne 0) {
        throw "GPU 固定启动拦截清理失败，Win32 退出码 $($process.ExitCode)；保留旧发布目录。"
    }
}

if (-not (Test-Path -LiteralPath $BackendProject)) {
    throw "未找到后端项目：$BackendProject"
}
if (-not (Test-Path -LiteralPath $NativeUiProject)) {
    throw "未找到 Native UI 项目：$NativeUiProject"
}

$publishBackend = $Target -in @("All", "Backend")
$publishNativeUi = $Target -in @("All", "NativeUi")

if (-not $SkipStop -and $Target -ne 'Launcher' -and (Test-Path -LiteralPath $DevelopmentRunner)) {
    Write-Step "停止当前仓库所属的运行实例，避免发布文件被占用。"
    Invoke-CheckedCommand `
        "powershell.exe" `
        @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $DevelopmentRunner, "-Action", "Stop", "-SkipBuild") `
        $RepositoryRoot
}

if ($publishBackend) {
    if (-not $SkipFrontend) {
        Write-Step "构建前端生产资源。"
        Invoke-CheckedCommand "npm.cmd" @("run", "build") $ClientRoot
    }
    else {
        Write-Step "跳过前端构建并验证现有确定性清单。"
    }
    $sourceFrontendBuildIdentity = Get-FrontendBuildIdentity $FrontendManifestPath
    Write-Step "前端构建身份：$sourceFrontendBuildIdentity"
}

if ($publishBackend) {
    if (-not (Test-Path -LiteralPath $GpuPlacementShimBuild)) {
        throw "未找到 GPU Provider 原生构建脚本：$GpuPlacementShimBuild"
    }
    Write-Step "构建 x64 GPU Provider 与托管启动 Bootstrap。"
    Invoke-CheckedCommand "cmd.exe" @("/d", "/c", "build.cmd") $GpuPlacementShimRoot
    if (-not (Test-Path -LiteralPath $GpuPlacementProvider)) {
        throw "GPU Provider 构建完成但产物不存在：$GpuPlacementProvider"
    }
    if (-not (Test-Path -LiteralPath $GpuPlacementBootstrap)) {
        throw "GPU 启动 Bootstrap 构建完成但产物不存在：$GpuPlacementBootstrap"
    }
    if (-not (Test-Path -LiteralPath $GpuLaunchBrokerBuild)) {
        throw "未找到 GPU 固定启动代理构建脚本：$GpuLaunchBrokerBuild"
    }
    Write-Step "构建 x64 GPU 固定启动代理。"
    Invoke-CheckedCommand "cmd.exe" @("/d", "/c", "build.cmd") $GpuLaunchBrokerRoot
    if (-not (Test-Path -LiteralPath $GpuLaunchBroker)) {
        throw "GPU 固定启动代理构建完成但产物不存在：$GpuLaunchBroker"
    }

    Clear-GpuLaunchInterceptionsBeforeBrokerReplacement
    Clear-PublishDirectory $BackendOutput
    Write-Step "发布 win-x64 自包含后端。"
    Invoke-CheckedCommand `
        "dotnet" `
        @("publish", $BackendProject, "/p:PublishProfile=win-x64-self-contained", "/p:SkipClientAppBuild=true", "--nologo", "--verbosity", "minimal") `
        $SoftwareRoot
}

if ($publishNativeUi) {
    Clear-PublishDirectory $NativeUiOutput
    Write-Step "发布 win-x64 单文件 Native UI。"
    Invoke-CheckedCommand `
        "dotnet" `
        @(
            "publish", $NativeUiProject,
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:PublishReadyToRun=true",
            "-p:PublishDir=$NativeUiOutput",
            "--nologo",
            "--verbosity", "minimal"
        ) `
        $SoftwareRoot
}

if ($Target -eq 'All' -or $Target -eq 'Launcher') {
    Clear-PublishDirectory $LauncherOutput
    Invoke-CheckedCommand 'dotnet' @(
        'publish', $LauncherProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true',
        "-p:PublishDir=$LauncherOutput", '--nologo', '--verbosity', 'minimal') $SoftwareRoot
}

if ($publishBackend -and -not (Test-Path -LiteralPath $BackendExe)) {
    throw "后端发布完成但入口不存在：$BackendExe"
}
if ($publishBackend -and -not (Test-Path -LiteralPath (Join-Path $BackendOutput "wwwroot\index.html"))) {
    throw "后端发布完成但前端资源不存在：$BackendOutput\wwwroot\index.html"
}
if ($publishBackend) {
    $publishedFrontendManifest = Join-Path $BackendOutput "wwwroot\frontend-build.json"
    $publishedFrontendBuildIdentity = Get-FrontendBuildIdentity $publishedFrontendManifest
    if ($publishedFrontendBuildIdentity -ne $sourceFrontendBuildIdentity) {
        throw "发布前端构建身份不匹配：源码 $sourceFrontendBuildIdentity；发布 $publishedFrontendBuildIdentity"
    }
}
if ($publishNativeUi -and -not (Test-Path -LiteralPath $NativeUiExe)) {
    throw "Native UI 发布完成但入口不存在：$NativeUiExe"
}

if ($Target -eq "All") {
    if (-not (Test-Path -LiteralPath $FinalImageBuilder)) {
        throw "未找到最终映像组装器：$FinalImageBuilder"
    }
    if (-not (Test-Path -LiteralPath $FinalImageValidator)) {
        throw "未找到最终映像验证器：$FinalImageValidator"
    }
    if (-not (Test-Path -LiteralPath $ExecutableManifestValidator)) {
        throw "未找到可执行文件 manifest 验证器：$ExecutableManifestValidator"
    }
    Write-Step "组装并封装后端与 Native UI 最终映像。"
    Invoke-CheckedCommand `
        "powershell.exe" `
        @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $FinalImageBuilder,
            "-BackendDirectory", $BackendOutput,
            "-NativeUiDirectory", $NativeUiOutput,
            "-LauncherDirectory", $LauncherOutput,
            "-OutputDirectory", $FinalOutput
        ) `
        $RepositoryRoot
    Write-Step "验证最终映像散列闭包。"
    Invoke-CheckedCommand `
        "powershell.exe" `
        @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $FinalImageValidator,
            "-RootDirectory", $FinalOutput
        ) `
        $RepositoryRoot
    Write-Step "验证最终映像 Native UI 的标准用户 manifest。"
    Invoke-CheckedCommand `
        "powershell.exe" `
        @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", $ExecutableManifestValidator,
            "-ExecutablePath", (Join-Path $FinalOutput "ResourceManagerNativeUi\ResourceManager.NativeUi.exe"),
            "-ExpectedExecutionLevel", "asInvoker"
        ) `
        $RepositoryRoot
}

Write-Step "发布完成。"
if ($publishBackend) {
    Write-Step "后端：$BackendExe"
}
if ($publishNativeUi) {
    Write-Step "Native UI：$NativeUiExe"
}
if ($Target -eq "All") {
    Write-Step "最终映像：$FinalOutput"
}

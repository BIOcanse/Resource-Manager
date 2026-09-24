param(
    [ValidateSet("Start", "Restart", "Stop")]
    [string]$Action = "Start",
    [switch]$SkipBuild,
    [switch]$Elevate,
    [switch]$NoSelfElevate,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("full", "normal-read-only")]
    [string]$StartupProfile = "full",
    [int]$WebViewDebugPort = 0
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$AppRoot = Join-Path $Root "Resource Manager\Resource Manager-APP"
$SoftwareRoot = Split-Path -Parent $AppRoot
$ClientRoot = Join-Path $AppRoot "ClientApp"
$AppProject = Join-Path $AppRoot "ResourceManager.App.csproj"
$NativeUiProject = Join-Path $AppRoot "NativeUi\ResourceManager.NativeUi.csproj"
$FrontendIndexPath = Join-Path $AppRoot "wwwroot\index.html"
$GpuPlacementShimRoot = Join-Path $AppRoot "Native\GpuPlacementShim"
$GpuPlacementShimBuild = Join-Path $GpuPlacementShimRoot "build.cmd"
$GpuPlacementProvider = Join-Path $GpuPlacementShimRoot "bin\win-x64\ResourceManager.GpuPlacementShim.dll"
$GpuPlacementBootstrap = Join-Path $GpuPlacementShimRoot "bin\win-x64\ResourceManager.GpuPlacementBootstrap.dll"
$GpuLaunchBrokerRoot = Join-Path $AppRoot "Native\GpuLaunchBroker"
$GpuLaunchBrokerBuild = Join-Path $GpuLaunchBrokerRoot "build.cmd"
$GpuLaunchBroker = Join-Path $GpuLaunchBrokerRoot "bin\win-x64\ResourceManager.GpuLaunchBroker.exe"
$BackendExe = Join-Path $AppRoot "bin\$Configuration\net10.0-windows\ResourceManager.exe"
$NativeUiExe = Join-Path $AppRoot "NativeUi\bin\$Configuration\net10.0-windows\ResourceManager.NativeUi.exe"
$KnownProcessNames = @("ResourceManager.NativeUi", "ResourceManager")
$ApiHealthUrl = "http://127.0.0.1:9321/api/local-system/status"
$ApiAccessTokenPath = Join-Path $Root "Resource Manager\Config\Runtime\loopback-api-token"
$ApiAccessTokenHeaderName = "X-Resource-Manager-Token"
$ApiPort = 9321
$StartupTimeout = [TimeSpan]::FromSeconds(20)
$LogPath = Join-Path $Root "development.log"

if (-not ("System.Net.Http.HttpClient" -as [type])) {
    Add-Type -AssemblyName System.Net.Http
}

if ($WebViewDebugPort -ne 0 -and ($WebViewDebugPort -lt 1024 -or $WebViewDebugPort -gt 65535)) {
    throw "WebViewDebugPort 必须为 0，或 1024 到 65535 之间的端口。"
}

if (-not ("ResourceManagerWindowProbe" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class ResourceManagerWindowProbe
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr windowHandle);
    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint mode);
}
"@
}

function Test-IsElevated {
    try {
        $groups = (& whoami /groups /fo csv /nh 2>$null) -join "`n"
        if ($groups -match "S-1-16-12288" -or $groups -match "S-1-16-16384") {
            return $true
        }
    }
    catch {
    }

    return $false
}

# The development backend requires elevation. Keep the runner, backend and UI in
# one integrity domain so process ownership, shutdown and readiness checks remain reliable.
if (-not $NoSelfElevate -and -not (Test-IsElevated)) {
    $scriptPath = $MyInvocation.MyCommand.Path
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "`"$scriptPath`"",
        "-Action",
        $Action,
        "-Configuration",
        $Configuration,
        "-StartupProfile",
        $StartupProfile,
        "-NoSelfElevate"
    )
    if ($SkipBuild) {
        $arguments += "-SkipBuild"
    }
    if ($WebViewDebugPort -ne 0) {
        $arguments += @("-WebViewDebugPort", $WebViewDebugPort)
    }

    try {
        $elevatedProcess = Start-Process `
            -FilePath "powershell.exe" `
            -ArgumentList ($arguments -join " ") `
            -WorkingDirectory $Root `
            -Verb RunAs `
            -WindowStyle Hidden `
            -PassThru
        $elevatedProcess.WaitForExit()
        exit $elevatedProcess.ExitCode
    }
    catch {
        Write-Host "[Resource Manager] 无法提权启动：$($_.Exception.Message)"
        exit 1
    }
}

function Write-Step([string]$Message) {
    Write-Host "[Resource Manager] $Message"
}

function Test-PathUnderRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $resolved = [System.IO.Path]::GetFullPath($Path)
        $rootResolved = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
        if ($resolved.Equals($rootResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }

        $rootBoundary = $rootResolved + [System.IO.Path]::DirectorySeparatorChar
        return $resolved.StartsWith($rootBoundary, [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Get-ResourceManagerProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $KnownProcessNames -contains $_.ProcessName } |
        ForEach-Object {
            $path = $null
            $creationFileTimeUtc = 0L
            try {
                $path = $_.MainModule.FileName
                $creationFileTimeUtc = $_.StartTime.ToUniversalTime().ToFileTimeUtc()
            }
            catch {
                $path = $null
            }

            [pscustomobject]@{
                Process = $_
                Path = $path
                CreationFileTimeUtc = $creationFileTimeUtc
                IsOwnedPath = Test-PathUnderRoot $path
            }
        } |
        Where-Object { $_.IsOwnedPath -and $_.CreationFileTimeUtc -gt 0 }
}

function Test-ResourceManagerApi {
    if (-not (Test-Path -LiteralPath $ApiAccessTokenPath -PathType Leaf)) {
        return $false
    }

    $token = $null
    try {
        $token = [System.IO.File]::ReadAllText($ApiAccessTokenPath).Trim()
    }
    catch {
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        return $false
    }

    $handler = $null
    $client = $null
    $request = $null
    $response = $null
    $sendTask = $null
    try {
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseProxy = $false
        $client = [System.Net.Http.HttpClient]::new($handler, $true)
        $handler = $null
        $client.Timeout = [TimeSpan]::FromSeconds(2)
        $request = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            $ApiHealthUrl)
        if (-not $request.Headers.TryAddWithoutValidation(
            $ApiAccessTokenHeaderName,
            $token)) {
            return $false
        }

        $sendTask = $client.SendAsync(
            $request,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(3)
        while (-not $sendTask.IsCompleted -and [DateTimeOffset]::UtcNow -lt $deadline) {
            [System.Threading.Thread]::Sleep(10)
        }
        if ((-not $sendTask.IsCompleted) -or
            $sendTask.IsCanceled -or
            $sendTask.IsFaulted -or
            $sendTask.Status -ne [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
            return $false
        }

        $response = $sendTask.Result
        return $response.StatusCode -eq [System.Net.HttpStatusCode]::OK
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
        if ($null -ne $request) {
            $request.Dispose()
        }
        if ($null -ne $client) {
            $client.Dispose()
        }
        elseif ($null -ne $handler) {
            $handler.Dispose()
        }
    }
}

function Test-LoopbackPortInUse {
    $client = [System.Net.Sockets.TcpClient]::new()
    $asyncResult = $null
    try {
        $asyncResult = $client.BeginConnect(
            [System.Net.IPAddress]::Loopback,
            $ApiPort,
            $null,
            $null)
        if (-not $asyncResult.AsyncWaitHandle.WaitOne([TimeSpan]::FromMilliseconds(500))) {
            return $false
        }

        $client.EndConnect($asyncResult)
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $asyncResult) {
            $asyncResult.AsyncWaitHandle.Dispose()
        }
        $client.Dispose()
    }
}

function Wait-DevelopmentBackendReady(
    [System.Diagnostics.Process]$BackendProcess,
    [TimeSpan]$Timeout = $StartupTimeout) {
    $deadline = [DateTimeOffset]::Now.Add($Timeout)
    do {
        $BackendProcess.Refresh()
        if ($BackendProcess.HasExited) {
            throw "调试后端启动后已退出，退出码 $($BackendProcess.ExitCode)。"
        }
        if (Test-ResourceManagerApi) {
            Write-Step "调试后端 API 已就绪。"
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::Now -lt $deadline)

    throw "调试后端未在 $($Timeout.TotalSeconds) 秒内就绪。"
}

function Test-ResourceManagerWindowVisible {
    $nativeUiProcesses = @(Get-ResourceManagerProcesses |
        Where-Object { $_.Process.ProcessName -eq "ResourceManager.NativeUi" })
    foreach ($entry in $nativeUiProcesses) {
        try {
            $entry.Process.Refresh()
            $windowHandle = $entry.Process.MainWindowHandle
            if ($windowHandle -ne [IntPtr]::Zero -and [ResourceManagerWindowProbe]::IsWindowVisible($windowHandle)) {
                return $true
            }
        }
        catch {
        }
    }

    return $false
}

function Wait-ResourceManagerStarted(
    [TimeSpan]$Timeout = $StartupTimeout,
    [switch]$ReturnFalseOnTimeout) {
    $deadline = [DateTimeOffset]::Now.Add($Timeout)
    do {
        $nativeUi = @(Get-ResourceManagerProcesses | Where-Object { $_.Process.ProcessName -eq "ResourceManager.NativeUi" })
        $apiReady = Test-ResourceManagerApi
        $windowVisible = Test-ResourceManagerWindowVisible
        if ($nativeUi.Count -gt 0 -and $apiReady -and $windowVisible) {
            Write-Step "启动验证通过：主窗口可见，本地 API 已就绪。"
            return $true
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::Now -lt $deadline)

    $processes = @(Get-ResourceManagerProcesses | ForEach-Object { "$($_.Process.ProcessName) PID $($_.Process.Id)" })
    $processText = if ($processes.Count -gt 0) { $processes -join "; " } else { "无" }
    $message = "启动后未在 $($Timeout.TotalSeconds) 秒内验证成功。当前进程：$processText；API 就绪：$(Test-ResourceManagerApi)；主窗口可见：$(Test-ResourceManagerWindowVisible)。日志：$LogPath"
    if ($ReturnFalseOnTimeout) {
        Write-Warning $message
        return $false
    }

    throw $message
}

function Wait-ResourceManagerStopped([TimeSpan]$Timeout, [switch]$NativeUiOnly) {
    $deadline = [DateTimeOffset]::Now.Add($Timeout)
    do {
        $remaining = @(Get-ResourceManagerProcesses | Where-Object {
            -not $NativeUiOnly -or $_.Process.ProcessName -eq 'ResourceManager.NativeUi'
        })
        if ($remaining.Count -eq 0) {
            return $true
        }

        Start-Sleep -Milliseconds 300
    } while ([DateTimeOffset]::Now -lt $deadline)

    return $false
}

function Invoke-NativeUiCommand(
    [ValidateSet("show", "exit")] [string]$Command,
    [string]$ExecutablePath = $NativeUiExe) {
    if (-not (Test-Path -LiteralPath $ExecutablePath) -or -not (Test-PathUnderRoot $ExecutablePath)) {
        return $false
    }

    $argument = if ($Command -eq "exit") { "--exit-existing" } else { "--show-existing" }
    try {
        $process = Start-Process `
            -FilePath $ExecutablePath `
            -ArgumentList $argument `
            -WorkingDirectory (Split-Path -Parent $ExecutablePath) `
            -WindowStyle Hidden `
            -PassThru
        if (-not $process.WaitForExit(5000)) {
            return $Command -eq "show"
        }

        return $process.ExitCode -eq 0
    }
    catch {
        Write-Warning "无法向 Web 壳发送 $Command 命令：$($_.Exception.Message)"
        return $false
    }
}

function Invoke-DevelopmentBackendStop(
    [Diagnostics.Process]$Process,
    [long]$CreationFileTimeUtc,
    [string]$ImagePath) {
    if (-not (Test-PathUnderRoot $ImagePath)) {
        throw '拒绝停止当前工作树以外的后端。'
    }
    $stopScript = Join-Path $Root 'scripts\BackendLifetime\Stop-OwnedBackend.ps1'
    $runtimeName = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' }
    $runtime = Join-Path $PSHOME $runtimeName
    $receiptRoot = Join-Path $SoftwareRoot ('Config\Diagnostics\BackendStop\' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($receiptRoot)
    $receiptPath = Join-Path $receiptRoot 'receipt.json'
    $stopArguments = @(
        '-NoProfile', '-File', $stopScript,
        '-ProcessId', [string]$Process.Id,
        '-CreationFileTimeUtc', [string]$CreationFileTimeUtc,
        '-ImagePath', $ImagePath,
        '-ReceiptPath', $receiptPath
    )
    $previousErrorMode = [ResourceManagerWindowProbe]::SetErrorMode(32771)
    try {
        & $runtime @stopArguments 1> (Join-Path $receiptRoot 'stdout.log') 2> (Join-Path $receiptRoot 'stderr.log')
        $stopExitCode = $LASTEXITCODE
    }
    finally {
        [void][ResourceManagerWindowProbe]::SetErrorMode($previousErrorMode)
    }
    if ($stopExitCode -ne 0 -or -not (Test-Path -LiteralPath $receiptPath)) {
        throw "后端正常停止失败，未自动强杀。证据：$receiptRoot"
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if (-not $receipt.passed -or $receipt.forced -or
        $receipt.stop.ProcessId -ne $Process.Id -or
        $receipt.stop.CreationFileTimeUtc -ne $CreationFileTimeUtc -or
        -not $receipt.stop.Exited -or $receipt.stop.ExitCode -ne 0 -or
        $null -ne $receipt.after) {
        throw "后端退出回执未通过身份和 ETW 清理核对：$receiptPath"
    }
    Write-Step "后端已正常退出，ETW 会话已释放。回执：$receiptPath"
}

function Stop-ResourceManager {
    $targets = @(Get-ResourceManagerProcesses)
    if ($targets.Count -eq 0) {
        Write-Step "未发现运行中的资源管理器进程。"
        return
    }

    $nativeUiForced = $false
    $nativeUiTargets = @($targets | Where-Object { $_.Process.ProcessName -eq "ResourceManager.NativeUi" })
    if ($nativeUiTargets.Count -gt 0) {
        Write-Step "请求 Web 壳自行退出。"
        $commandPath = $nativeUiTargets |
            Select-Object -ExpandProperty Path -First 1
        if ([string]::IsNullOrWhiteSpace($commandPath)) {
            $commandPath = $NativeUiExe
        }
        [void](Invoke-NativeUiCommand "exit" $commandPath)
        if (-not (Wait-ResourceManagerStopped ([TimeSpan]::FromSeconds(8)) -NativeUiOnly)) {
            foreach ($target in $nativeUiTargets) {
                $target.Process.Refresh()
                if (-not $target.Process.HasExited -and
                    $target.Process.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $target.CreationFileTimeUtc) {
                    $target.Process.Kill()
                    $nativeUiForced = $true
                }
            }
            Write-Warning 'Web 壳未自行退出，已对精确自有 UI 执行故障清理；本次停止不会记为正常成功。'
        }
    }

    foreach ($target in @($targets | Where-Object { $_.Process.ProcessName -eq 'ResourceManager' })) {
        $process = $target.Process
        Write-Step "请求后端正常退出 PID $($process.Id)"
        Invoke-DevelopmentBackendStop -Process $process -CreationFileTimeUtc $target.CreationFileTimeUtc -ImagePath $target.Path
    }

    if (-not (Wait-ResourceManagerStopped ([TimeSpan]::FromSeconds(5)))) {
        $remaining = @(Get-ResourceManagerProcesses)
        $remainingText = ($remaining | ForEach-Object { "$($_.Process.ProcessName) PID $($_.Process.Id)" }) -join "; "
        throw "仍有资源管理器进程未停止：$remainingText。请确认脚本以管理员权限运行。"
    }
    if ($nativeUiForced) {
        throw '进程已清理，但 UI 未正常退出，停止流程不能记为成功。'
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

function Clear-GpuLaunchInterceptionsBeforeBrokerReplacement {
    $arguments = @{
        FilePath = $GpuLaunchBroker
        ArgumentList = "--remove-all-resource-manager-ifeo"
        WindowStyle = "Hidden"
        Wait = $true
        PassThru = $true
    }
    if (-not (Test-IsElevated)) {
        $arguments.Verb = "RunAs"
    }

    Write-Step "替换启动代理前清理本产品拥有的固定启动拦截。"
    $process = Start-Process @arguments
    if ($process.ExitCode -ne 0) {
        throw "GPU 固定启动拦截清理失败，Win32 退出码 $($process.ExitCode)；不替换现有构建。"
    }
}

function Build-ResourceManager {
    if ($SkipBuild) {
        Write-Step "跳过构建。"
        return
    }

    if (-not (Test-Path -LiteralPath $AppProject)) {
        throw "未找到后端项目：$AppProject"
    }
    if (-not (Test-Path -LiteralPath $NativeUiProject)) {
        throw "未找到 Native UI 项目：$NativeUiProject"
    }
    if (-not (Test-Path -LiteralPath $GpuPlacementShimBuild)) {
        throw "未找到 GPU Provider 原生构建脚本：$GpuPlacementShimBuild"
    }
    if (-not (Test-Path -LiteralPath $GpuLaunchBrokerBuild)) {
        throw "未找到 GPU 固定启动代理构建脚本：$GpuLaunchBrokerBuild"
    }

    Write-Step "构建前端资源。"
    Invoke-CheckedCommand "npm.cmd" @("run", "build") $ClientRoot
    if (-not (Test-Path -LiteralPath $FrontendIndexPath -PathType Leaf)) {
        throw "前端构建完成但入口不存在：$FrontendIndexPath"
    }

    Write-Step "构建 x64 GPU Provider 与托管启动 Bootstrap。"
    Invoke-CheckedCommand "cmd.exe" @("/d", "/c", "build.cmd") $GpuPlacementShimRoot
    if (-not (Test-Path -LiteralPath $GpuPlacementProvider)) {
        throw "GPU Provider 构建完成但产物不存在：$GpuPlacementProvider"
    }
    if (-not (Test-Path -LiteralPath $GpuPlacementBootstrap)) {
        throw "GPU 启动 Bootstrap 构建完成但产物不存在：$GpuPlacementBootstrap"
    }

    Write-Step "构建 x64 GPU 固定启动代理。"
    Invoke-CheckedCommand "cmd.exe" @("/d", "/c", "build.cmd") $GpuLaunchBrokerRoot
    if (-not (Test-Path -LiteralPath $GpuLaunchBroker)) {
        throw "GPU 固定启动代理构建完成但产物不存在：$GpuLaunchBroker"
    }

    Clear-GpuLaunchInterceptionsBeforeBrokerReplacement

    Write-Step "构建后端。"
    Invoke-CheckedCommand "dotnet" @("build", $AppProject, "-c", $Configuration, "--nologo", "--verbosity", "minimal") $Root

    Write-Step "构建 Web 壳。"
    Invoke-CheckedCommand "dotnet" @("build", $NativeUiProject, "-c", $Configuration, "--nologo", "--verbosity", "minimal") $Root
}

function Start-ResourceManager {
    $running = @(Get-ResourceManagerProcesses)
    $runningNativeUi = @($running | Where-Object { $_.Process.ProcessName -eq "ResourceManager.NativeUi" })
    if ($runningNativeUi.Count -gt 0) {
        $runningText = ($runningNativeUi | ForEach-Object { "PID $($_.Process.Id)" }) -join ", "
        Write-Step "Web 壳已经在运行：$runningText；请求打开现有窗口。"
        $commandPath = $runningNativeUi |
            Select-Object -ExpandProperty Path -First 1
        if ([string]::IsNullOrWhiteSpace($commandPath)) {
            $commandPath = $NativeUiExe
        }
        $showRequested = Invoke-NativeUiCommand "show" $commandPath
        $existingInstanceReady = $false
        if ($showRequested) {
            try {
                $existingInstanceReady = Wait-ResourceManagerStarted `
                    -Timeout ([TimeSpan]::FromSeconds(8)) `
                    -ReturnFalseOnTimeout
            }
            catch {
                Write-Warning "现有实例未能完成启动就绪检查：$($_.Exception.Message)"
            }
        }
        if ($existingInstanceReady) {
            return
        }

        Write-Step "现有实例未确认显示可见主窗口，按不完整实例清理后重新启动。"
        Stop-ResourceManager
    }
    elseif ($running.Count -gt 0) {
        $runningText = ($running | ForEach-Object { "$($_.Process.ProcessName) PID $($_.Process.Id)" }) -join "; "
        Write-Step "发现没有主窗口的残留实例：$runningText；先清理再启动。"
        Stop-ResourceManager
    }

    Build-ResourceManager

    if (-not (Test-Path -LiteralPath $BackendExe)) {
        throw "未找到后端可执行文件：$BackendExe"
    }
    if (-not (Test-Path -LiteralPath $NativeUiExe)) {
        throw "未找到主窗口可执行文件：$NativeUiExe"
    }

    if (Test-LoopbackPortInUse) {
        throw "127.0.0.1:9321 已被当前仓库以外的后端占用；拒绝启动路径身份不匹配的调试 UI。"
    }

    $backendProcess = $null
    $backendCreationFileTimeUtc = 0L
    $previousPackageRoot = $env:RESOURCE_MANAGER_PACKAGE_ROOT
    $env:RESOURCE_MANAGER_PACKAGE_ROOT = $SoftwareRoot
    try {
        Write-Step "启动外层脚本拥有的调试后端：$BackendExe"
        Write-Step "开发启动配置：$StartupProfile"
        $backendProcess = Start-Process `
            -FilePath $BackendExe `
            -ArgumentList @("--no-native-ui", "--startup-profile", $StartupProfile) `
            -WorkingDirectory (Split-Path -Parent $BackendExe) `
            -WindowStyle Hidden `
            -PassThru
        $backendCreationFileTimeUtc = $backendProcess.StartTime.ToUniversalTime().ToFileTimeUtc()
        Wait-DevelopmentBackendReady -BackendProcess $backendProcess

        Write-Step "启动只附着主窗口：$NativeUiExe"
        $previousDebugPort = $env:RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT
        $previousDeveloperSurfaces = $env:RESOURCE_MANAGER_WEBVIEW_DEVELOPER_SURFACES
        try {
            if ($WebViewDebugPort -ne 0) {
                $env:RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT = [string]$WebViewDebugPort
                $env:RESOURCE_MANAGER_WEBVIEW_DEVELOPER_SURFACES = "1"
                Write-Step "本次 Debug WebView2 CDP 仅绑定 loopback 端口 $WebViewDebugPort。"
            }
            else {
                Remove-Item Env:RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT -ErrorAction SilentlyContinue
            }
            Start-Process `
                -FilePath $NativeUiExe `
                -ArgumentList "--show-existing" `
                -WorkingDirectory (Split-Path -Parent $NativeUiExe) `
                -WindowStyle Hidden
        }
        finally {
            if ($null -eq $previousDebugPort) {
                Remove-Item Env:RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT -ErrorAction SilentlyContinue
            }
            else {
                $env:RESOURCE_MANAGER_WEBVIEW_REMOTE_DEBUGGING_PORT = $previousDebugPort
            }
            if ($null -eq $previousDeveloperSurfaces) {
                Remove-Item Env:RESOURCE_MANAGER_WEBVIEW_DEVELOPER_SURFACES -ErrorAction SilentlyContinue
            }
            else {
                $env:RESOURCE_MANAGER_WEBVIEW_DEVELOPER_SURFACES = $previousDeveloperSurfaces
            }
        }
        [void](Wait-ResourceManagerStarted)
    }
    catch {
        $startupError = $_
        if ($null -ne $backendProcess) {
            try {
                $backendProcess.Refresh()
                if (-not $backendProcess.HasExited) {
                    Invoke-DevelopmentBackendStop -Process $backendProcess -CreationFileTimeUtc $backendCreationFileTimeUtc -ImagePath $BackendExe
                }
            }
            catch {
                Write-Warning "启动失败后的后端清理也未通过：$($_.Exception.Message)"
            }
            finally {
                $backendProcess.Dispose()
            }
        }
        throw $startupError
    }
    finally {
        if ($null -eq $previousPackageRoot) {
            Remove-Item Env:RESOURCE_MANAGER_PACKAGE_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:RESOURCE_MANAGER_PACKAGE_ROOT = $previousPackageRoot
        }
    }

    if ($null -ne $backendProcess) {
        $backendProcess.Dispose()
    }
}

$exitCode = 0
$transcriptStarted = $false
try {
    try {
        Start-Transcript -LiteralPath $LogPath -Append | Out-Null
        $transcriptStarted = $true
        Write-Step "日志：$LogPath"
    }
    catch {
        Write-Warning "无法写入运行日志：$($_.Exception.Message)"
    }

    switch ($Action) {
        "Stop" {
            Stop-ResourceManager
        }
        "Restart" {
            Stop-ResourceManager
            Start-ResourceManager
        }
        default {
            Start-ResourceManager
        }
    }

    Write-Step "完成。"
}
catch {
    $exitCode = 1
    Write-Host "[Resource Manager] 失败：$($_.Exception.Message)"
    Write-Host "[Resource Manager] 日志：$LogPath"
}
finally {
    if ($transcriptStarted) {
        try {
            Stop-Transcript | Out-Null
        }
        catch {
        }
    }
}

exit $exitCode

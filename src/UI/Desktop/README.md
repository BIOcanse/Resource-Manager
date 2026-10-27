# Desktop 常驻进程（phase A）

使用 Zig 0.16.0 和 `src/Shared/Control` 的 `rm_control`。运行入口由宿主线程执行，控制器回调只排队。运行时存储有固定上限；控制器仅以 ReleaseSafe 编译。

在仓库根目录、已登记 running 的输出目录下，通过 JobQueue 构建：

```powershell
$work = 'F:\code\Resource-Manager-Development\desktop-resident'
& scripts/build/Generate-DesktopPoints.ps1 -WorkRoot $work
& scripts/build/Generate-DesktopText.ps1
$env:ZIG_GLOBAL_CACHE_DIR = "$work\zig-global-cache"
& D:\Code\CommonAssets\Tools\JobQueue\JobQueue.ps1 -Action Run -Label 'desktop Debug' `
  -FilePath D:\Code\CommonAssets\Tools\Zig\zig-0.16.0\zig.exe `
  -ArgumentList @('build', '-Doptimize=Debug', "-Dpoints-root=$work/desktop-points", '--cache-dir', "$work\zig-cache", '--prefix', "$work\debug", '-j2') `
  -WorkingDirectory "$PWD\src\UI\Desktop"
```

产物为 `debug/bin/ResourceManager.Desktop.exe`。`build test` 是最终全套；`test-blocks`、`test-pipe`、`test-integration` 可在失败后定向回归。登记表每次构建经共享生成器生成，输入依赖包含真实登记表和生成器；Debug 默认包含名字，其他优化配置默认不包含名字，可通过 `-Dstate-names` 显式选择。

九个可调默认值只在 `src/Development/DesktopBuild/desktop.points.json` 声明，通过 FreedomPoint SDK 生成 Zig 值。固定队列、缓冲区和表容量属于结构上限，耗尽时明确失败。

部署路径在打包步骤 6 接入前通过 `--package-root`、`--window-executable`、`--backend-path` 指定；未指定时只采用可执行文件所在目录与同级程序的布局。`--background-startup` 不启动窗口。已有单实例协议和默认管道名保持不变；测试必须显式传入 `--instance-pipe ResourceManager.Desktop.Test.<unique>`，并指定独立 `--backend-port`、不存在的 `--backend-service`，不得使用安装产品端口或服务。

`--preferences-path` 可显式指定缓存，否则读取包根下当前用户 SID 的前端缓存；只在初始化及 `preferences-changed` 时重读。常驻进程不写前端偏好。管道消息与行为见最终报告。

`--diagnostics <file>` 是显式开发入口：导出带登记表版本的控制器快照、进程与 HWND，并在同目录 `<file>.log` 写诊断。仅在此模式下，托盘窗口接受 `WM_APP+10` 的坐标参数，用于副屏菜单的目标窗口截图；不移动鼠标或抓取桌面。真实验收入口为 `tests/Desktop/Invoke-ResidentSmoke.ps1`，同样经 JobQueue 运行。

phase A 的热键、任务管理器替换、桌面观察、通知流和覆盖层均为等待停止的占位入口。WinHTTP 发送前鉴权 gate 与挂死子进程停止策略存在尚未裁决的合同冲突，不能将此阶段声明为完整 ready 后端或挂死恢复已通过。具体证据、测试结果和 phase B/C 边界以 `.local/reports/final-report.md` 为准。

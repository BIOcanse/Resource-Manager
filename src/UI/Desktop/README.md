# Desktop 常驻进程

使用 Zig 0.17.0 和 `src/Shared/Control` 的 `rm_control`。运行入口由宿主线程执行，控制器回调只排队。运行时存储有固定上限；控制器仅以 ReleaseSafe 编译。


常驻进程的控制树：

```text
Program
└─ Frontend
   ├─ MainFrontend
   │  ├─ SingleInstance
   │  ├─ SystemIntegration
   │  │  ├─ ForceTerminateHotkey
   │  │  ├─ OverlayToggleHotkey
   │  │  └─ TaskManagerReplacement
   │  ├─ BackendConnection
   │  │  ├─ DesktopObservation
   │  │  └─ Overlay
   │  └─ WindowProcess
   └─ Tray
      └─ TrayNotifications
```

Program → Frontend 使用固定条件 ProgramActive，只读 ProgramStarted。Frontend 是包装块，通过 AlwaysWanted 要求 MainFrontend 和 Tray；MainFrontend 同样要求 SystemIntegration。系统集成的三个子块各自读取后端开关和有效键位，包装块只报告运行并等待停止。Tray 保留图标、菜单和通知分支。兄弟之间只读写事实，不互相启停。

托盘退出、第二实例的 exit、页面 applyExit、Windows 确认结束会话均进入同一个 beginEnding，调用 controller.shutdown()。Config.shutdown_item 为 ProgramShuttingDown，写入口只由控制器持有，条件不读这一项。shutdown 撤回根的许可，即使 ProgramStarted 仍为真也停止 Frontend 整支。根等待包装块及其全部子孙汇合后才退出消息循环；生产循环与真实进程场景共用退出判断。窗口进程自己的树及无 pipe 参数的过渡组合保持原行为。

在仓库根目录、已登记 running 的输出目录下，通过 JobQueue 构建：

```powershell
$work = '<workset-root>\desktop-resident'
& scripts/build/Generate-DesktopPoints.ps1 -WorkRoot $work
& scripts/build/Generate-DesktopLedger.ps1 -WorkRoot "$work/desktop-ledger"
& scripts/build/Generate-DesktopText.ps1
& scripts/build/Generate-DesktopFrontendDefaults.ps1
$env:ZIG_GLOBAL_CACHE_DIR = "$work\zig-global-cache"
& <tools>\JobQueue\JobQueue.ps1 -Action Run -Label 'desktop Debug' `
  -FilePath <tools>\Zig\zig-0.17.0\zig.exe `
  -ArgumentList @('build', "-Dpoints-root=$work/desktop-points", "-Dledger-root=$work/desktop-ledger", '--cache-dir', "$work\zig-cache", '--prefix', "$work\debug", '-j1') `
  -WorkingDirectory "$PWD\src\UI\Desktop"
```

产物为 `debug/bin/ResourceManager.Desktop.exe`。`build test` 覆盖全部 Zig 测试；内存受限时分别调用 `test-blocks`、`test-pipe`、`test-integration`，每次只编译一个入口。`-Dintegration-filter=<名称子串>` 可定向运行树场景。登记表每次构建经共享生成器生成，输入依赖包含真实登记表和生成器；Debug 默认包含名字，其他优化配置默认不包含名字，可通过 `-Dstate-names` 显式选择。

开发期导出从同一份 `tree.zig` 的 Program 根（UID 40）开始，经共享 `export_tree_main.zig` 输出整个常驻树。`WindowProcess`（UID 89）声明 `launches_process = "window"`；这只是导出元数据，控制器不读取它。生成器为导出单独提供带名字的开发表，不把名字带入正式运行表，也不安装导出工具。Frontend 使用 UID 101，MainFrontend 和 Tray 保留 UID 41、42。窗口树仍是 C# 声明；本步骤不迁移它，也不为它编造 Zig 导出。

在同一个已登记输出目录内通过 JobQueue 运行 `zig build export-tree -j1 --cache-dir <缓存目录>`，stdout 是单进程 JSON。窗口进程提供真实导出后，可将两份输出交给 `scripts/tools/Export-ControlTree.ps1 -TreeJson <desktop.json>,<window.json>` 拼接；共享脚本会明确拒绝缺少被启动进程的输入。`zig build test-export -j1` 导出真实根声明，并校验根、Frontend 和唯一的 window 启动块；默认 `zig build test -j1` 包含这一检查。测试不启动常驻进程或窗口进程。

普通交互身份的文件夹具验收独立为 `test-key-fixture`：先由维护会话用既有 Initialize-BackendKeyFixture.ps1 准备，再以 `-Dkey-fixture-work-root=<同一 WorkRoot>` 运行该入口（未给出时跳过），原 owner/ACL/重复读取/取消断言不变。`check-key-fixture` 只编译此入口；默认 `test` 不访问该真实夹具。`-Dpipe-filter=<名称子串>` 可只补失败的管道测试。

可调值在 `src/Development/DesktopBuild/desktop.points.json` 声明，通过 FreedomPoint SDK 投影；浮窗的软件条目、默认值和编辑约束由后端设置拥有。窗口停止期限和超期策略归窗口包装块；策略为 `terminate` 或 `wait`。浮窗容量、缓冲区、字号拟合范围、字体、颜色、格式精度和订阅间隔也有声明，耗尽时明确失败。Windows ABI、协议布局和数学单位定义是格式事实。

部署路径在打包步骤 6 接入前通过 `--package-root`、`--window-executable`、`--backend-path` 指定；未指定时只采用可执行文件所在目录与同级程序的布局。`--background-startup` 不启动窗口。已有单实例协议和默认管道名保持不变；测试必须显式传入 `--instance-pipe ResourceManager.Desktop.Test.<unique>`，并指定独立 `--backend-port`、不存在的 `--backend-service`，不得使用安装产品端口或服务。

`--preferences-path` 可显式指定缓存，否则读取包根下当前用户 SID 的前端缓存；只在初始化及 `preferences-changed` 时重读。常驻进程不写前端偏好。管道消息与行为见最终报告。

浮窗读取已有 `/api/settings/app/subscribe` 的 `performanceOverlay`，不读取偏好的 overlay 节，也不轮询设置。每个启用软件的订阅请求携带有序 items；每个选中项保留一行，缺值显示后端原因，未知原因或显式来源不可读显示“—”。目标窗口跟随由 WinEvent 驱动。热键 `toggle-performance-overlay-foreground` 和托盘项通过后端 toggle API 改设置，后端订阅驱动已有目标更新。旧偏好的迁移只由窗口进程执行，提交确认后移除，兼容代码的删除条件见该迁移所有者。

`--diagnostics <file>` 是显式开发入口：导出带登记表版本的控制器快照、进程与 HWND，并在同目录 `<file>.log` 写诊断。仅在此模式下，托盘窗口接受 `WM_APP+10` 的坐标参数，用于副屏菜单的目标窗口截图；不移动鼠标或抓取桌面。真实验收入口为 `tests/Desktop/Invoke-ResidentSmoke.ps1`，同样经 JobQueue 运行。

后端连接块自持一个 Winsock HTTP 传输：连接实际 TCP socket 后，先核验服务路径、PID、进程创建时间和 TCP 对端，再写含密钥的请求头。健康响应通过后才发布 ready；任何核验、HTTP、解析或超时失败都发布不可用及明确 reason，没有回退。并发探测只有一个，总网络期限使用既有 `backend_timeout_ms`，健康间隔仍为 `backend_probe_interval_ms`。原 WinHTTP 诊断用途保留。

窗口包装块通过 `JOB_LIST` 在创建子进程时直接指定非继承 Job，并设置 `KILL_ON_JOB_CLOSE`。普通窗口块停止时先发送 stop/关闭管道，在已声明期限内汇合。超期按选择强制结束或继续等待；程序结束的独立 stop 会取消等待并关闭 Job 收尾。三种停止结果均回报宿主日志；没有 detach，崩溃仍先撤回开窗意愿。

热键、任务管理器键盘替换、桌面观察上传流、托盘通知和 Zig 覆盖层均有实际入口；持久启动入口和安装打包仍由后续批次接入。验证结果以本轮工作账本的 report.md 为准。真实托盘、CPU/内存、安装包和游戏验收由维护会话执行，定向构建与测试不接触已安装产品。

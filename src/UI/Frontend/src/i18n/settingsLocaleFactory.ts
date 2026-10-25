import type { ConcreteAppLanguageMode, SettingsCopy, SettingsCopyPatch, SettingsCopyPatchObject } from "./settingsTypes.ts";
import { enUpdatesCopy, zhCnUpdatesCopy } from "./settingsUpdateCopy.ts";

const zhCnSettingsCopy: SettingsCopy = {
  updates: zhCnUpdatesCopy,
  navigationLabel: "设置分区",
  sections: {
    performance: "性能",
    appearance: "外观",
    systemIntegration: "系统集成",
    updates: "更新与版本",
    debug: "调试",
    credits: "致谢"
  },
  saveState: {
    saving: "保存中",
    saved: "已保存",
    constrained: "已保存；当前运行模式已限制不适用项",
    partial: "已保存，但尚未完全应用",
    error: "保存失败",
    dirty: "未保存",
    conflict: "设置已在其他位置更新，请重新加载"
  },
  loadState: {
    loading: "正在读取已保存设置…",
    errorTitle: "无法读取设置",
    errorDescription: "没有显示或使用默认值替代真实设置。请恢复本地服务后重试。",
    staleTitle: "设置已失去同步",
    staleDescription: "当前副本可能过期，重新加载成功前不会提交更改。"
  },
  actions: {
    save: "保存",
    restoreDefaults: "恢复为默认",
    retry: "重试",
    reload: "重新加载",
    reapply: "重新应用"
  },
  performance: {
    smartMonitoringTitle: "按需监控",
    smartMonitoringDescription: "目前始终按可见需求采样。旧开关已停用，其后续用途待定。",
    adaptiveBooleanModeOptions: [
      { id: "enabled", label: "开启" },
      { id: "disabled", label: "关闭" }
    ],
    preciseGpuPlacementTitle: "精确 GPU 选择",
    preciseGpuPlacementDescription: "允许受支持的渲染进程精确选卡；运行时换卡仍取决于该进程的兼容能力。每个软件可单独设。",
    preciseGpuPlacementModeOptions: {
      basic: { label: "关闭", description: "仅使用 Windows 支持的显卡偏好。" },
      precise: { label: "开启", description: "允许为软件精确选择目标显卡。" }
    },
    automaticMemoryCleanupTitle: "紧急清理阈值",
    automaticMemoryCleanupDescription: "后台清理按资源压力分档；这里设定物理内存或提交额度剩余低于多少时进入紧急清理。只在优化模式包含内存调度时生效。",
    physicalMemoryAutomaticCleanupLabel: "物理内存剩余 %",
    virtualMemoryAutomaticCleanupLabel: "提交额度剩余 %",
    memoryOptimizationTargetTitle: "内存优化目标",
    memoryOptimizationTargetDescription: "超过目标后慢慢释放，不做紧急清理。",
    physicalMemoryOptimizationTargetLabel: "物理内存目标使用率 %",
    virtualMemoryOptimizationTargetLabel: "提交量目标使用率 %",
    gpuOverflowTitle: "独显溢出阈值",
    gpuOverflowDescription: "任一项达到阈值时，将后台低分进程从独立显卡移到集成显卡。仅在调度已开启且集显可用时生效。",
    gpuOverflowUsageLabel: "独显使用率 %",
    gpuOverflowDedicatedMemoryLabel: "独显专用显存占用率 %",
    realtimeActionBudgetTitle: "实时动作预算",
    realtimeActionBudgetDescription: "每轮实时动作上限可按固定数量，或本轮识别的全部进程数的百分比（向上取整）设置。切换模式会保留两项数值。",
    realtimeActionBudgetModeOptions: [{"id":"fixed","label":"固定数量"},{"id":"percent","label":"进程百分比"}],
    realtimeActionBudgetCountLabel: "固定动作数量",
    realtimeActionBudgetPercentLabel: "全部进程百分比 %",
    refreshCadenceTitle: "信息更新频率",
    refreshCadenceDescription: "越快越费资源。",
    presetNumericModeOptions: [
      { id: "aotu", label: "自动调度", description: "按性能模式和窗口状态自动选。" },
      { id: "preset", label: "锁定预设", description: "用右侧选的预设。" },
      { id: "custom", label: "自定义", description: "用手填的毫秒值。" }
    ],
    refreshCadencePresetLabel: "预设",
    refreshCadenceCustomLabel: "自定义 (ms)",
    refreshCadencePresetOptions: [
      { id: "responsive", label: "响应", description: "前台交互优先，刷新更快。" },
      { id: "balanced", label: "平衡", description: "日常用这个。" },
      { id: "lowPower", label: "低功耗", description: "后台省电。" },
      { id: "quiet", label: "静默", description: "最低频率，只留必要的。" }
    ],
    refreshCadenceItems: {
      monitor: { label: "监控快照 / 资源条", description: "监视控制台的数值和资源条" },
      resourceTable: { label: "资源表", description: "软件和进程列表" },
      localSystem: { label: "本机状态", description: "开机时间等低频状态" }
    }
  },
  appearance: {
    cacheDescription: "\u5916\u89C2\u3001\u5237\u65B0\u95F4\u9694\u548C\u6D6E\u7A97\u5916\u89C2\u7F13\u5B58\u5728\u6B64\u8BBE\u5907\u4E0A\uFF1B\u6E05\u7A7A\u7F13\u5B58\u540E\u6062\u590D\u9ED8\u8BA4\u503C\u3002",
    resetCache: "\u6062\u590D\u754C\u9762\u504F\u597D\u9ED8\u8BA4\u503C",
    uiStyleTitle: "界面风格",
    uiStyleDescription: "改变控件的圆角、线条、阴影和字体，布局不变。每种风格有自己的配色。",
    themeTitle: "配色",
    themeDescription: "选择当前风格提供的配色。跟随系统会按 Windows 的浅色或深色设置切换。",
    animationTitle: "动画效果",
    animationDescription: "选择完整动画、关闭动画，或进一步简化视觉以降低界面开销。",
    resourceBarFrameRateTitle: "资源条动画帧率",
    resourceBarFrameRateDescription: "资源条过渡动画每秒画几帧。跟随屏幕按显示器刷新率画；自定义可限制帧数以降低开销。只在正常动画下生效。",
    resourceBarFrameRateModeOptions: [
      { id: "display", label: "跟随屏幕", description: "每次屏幕刷新都画一帧。" },
      { id: "custom", label: "自定义", description: "每秒最多画设定的帧数（10–240）。" }
    ],
    resourceBarFrameRateCustomLabel: "帧率 (FPS)",
    barColorTitle: "条形图配色",
    barColorDescription: "按软件类型使用固定颜色，或为每个软件分配不同颜色。",
    byteUnitTitle: "容量单位",
    byteUnitDescription: "1 GiB = 1024 MiB，1 GB = 1000 MB。切换后所有容量数字一起变，不只是改标签。",
    readingSourceTitle: "读数来源",
    readingSourceDescription: "监视控制台和设备详情里，同一读数有多个来源时怎么列。默认只列本机最准确的来源；显示所有可用来源时，最准确的标为“首选”。调度始终用最准确的来源，不受这里影响。",
    readingSourceModeOptions: [
      { id: "preferred", label: "仅最准确", description: "每个读数只列出本机最准确的来源。" },
      { id: "all", label: "所有可用", description: "列出每个读数的所有可用来源，并标出最准确的。" }
    ],
    fontSmoothingTitle: "文字平滑",
    fontSmoothingDescription: "自动平衡文字清晰度和界面开销，也可以固定渲染方式。",
    languageTitle: "界面语言",
    languageDescription: "选择资源管理器界面使用的语言。跟随系统会使用 Windows 显示语言。",
    settingsLanguageTitle: "界面语言",
    settingsLanguageDescription: "切换整个界面的语言，包括桌面外壳的托盘与窗口标题；跟随系统会使用 Windows 显示语言。",
    languageSelectLabel: "语言",
    uiStyleOptions: {
      standard: "标准",
      soft: "柔和",
      outline: "线框",
      terminal: "终端"
    },
    themeOptions: {
      system: "跟随系统",
      light: "浅色",
      dark: "深色",
      lowContrast: "低对比",
      paper: "纸面",
      graphite: "石墨",
      blueprint: "蓝图",
      phosphor: "磷绿",
      amber: "琥珀",
      ice: "冰蓝"
    },
    animationOptions: {
      auto: { label: "自动调度", description: "前台正常动画，后台自动切超高性能。" },
      normal: { label: "正常动画", description: "全局过渡动画" },
      none: { label: "无动画", description: "关掉动画，外观不变" },
      ultra: { label: "超高性能", description: "关动画并去掉阴影、高光、模糊，开销最低" }
    },
    barColorOptions: {
      type: { label: "类型固定色", description: "按系统、适配和一般应用上色，相邻分段用分隔线区分" },
      distinct: { label: "异色区分", description: "每个软件不同颜色，不表达类型，无分隔线" }
    },
    byteUnitOptions: {
      native: {
        label: "按物理性质",
        description: "内存、显存、缓存用 GiB（内存颗粒天生是 2 的幂）；磁盘容量、文件大小、累计流量用 GB（与厂商标称一致）"
      },
      binary: { label: "全用 GiB", description: "一律按 1024 换算，标签一律 KiB / MiB / GiB" },
      decimal: { label: "全用 GB", description: "一律按 1000 换算，标签一律 kB / MB / GB" }
    },
    fontSmoothingOptions: {
      auto: { label: "自动调度", description: "正常状态使用系统 ClearType，超高性能状态减少文字平滑开销" },
      system: { label: "系统 ClearType", description: "Windows 原生次像素渲染，文字最锐利" },
      grayscale: { label: "灰度平滑", description: "灰度抗锯齿，更柔和，适合部分高分屏" },
      disabled: { label: "关闭", description: "关闭额外文字平滑，使用最直接的字形栅格化" }
    },
  },
    systemIntegration: {
      crashRecoveryTitle: "崩溃后重启",
    crashRecoveryDescription: "后台服务崩溃时自动重启。正常停止不会触发重启。",
    crashRestartDelayTitle: "重启前等待（秒）",
    crashRestartCountTitle: "允许重启次数",
    crashCounterResetDescription: "连续 {days} 天未崩溃后重置计数。达到次数上限后，需手动启动服务。",
    crashCounterResetTitle: "计数重置周期（天）",
    crashRecoveryActualTitle: "当前实际配置",
    crashRecoveryUnavailable: "暂时无法确认自动恢复配置。",
    crashRecoveryApplied: "自动恢复配置已应用。",
    crashRecoverySkipped: "当前运行方式不应用自动恢复配置。",
    crashRecoveryFailed: "自动恢复配置未能应用。",
    crashRecoveryMismatch: "当前实际配置与所选设置不同。",
    crashRecoveryNonCrashWarning: "当前配置也会在正常停止并返回错误时重启。",
    crashRecoveryWriteFailed: "无法写入自动恢复配置。",
    crashRecoveryReadFailed: "无法读取自动恢复配置。",
    taskManagerTitle: "任务管理器快捷键替换",
      autoStartTitle: "开机自启",
      autoStartDescription: "开机后运行后台服务，登录后进入托盘。",
      taskManagerDescription: "接管 Ctrl+Shift+Esc：注册成功时未运行也会启动资源管理器，已运行时显示或置顶主窗口；不接管 Ctrl+Alt+Del。",
      forceTerminateTitle: "强制结束快捷键",
      forceTerminateDescription: "结束当前前台焦点程序，以及所有拥有可见无响应窗口的程序。默认关闭。",
      forceTerminateWarning: "触发后立即强制结束进程，未保存的内容会丢失。启用时必须同时包含 Ctrl、Alt 或 Win 修饰键以及一个非修饰键。",
      hotkeyEmpty: "尚未配置按键",
      hotkeyAddKey: "添加按键",
      hotkeyKeyLabel: "按键",
      hotkeyRemoveKey: "删除按键",
      hotkeyUnordered: "同时按下",
      hotkeyOrdered: "前键先按",
      publicServiceTitle: "本机公共服务",
      publicServiceDescription: "允许本机软件使用资源管理器提供的共享功能。",
      publicFileIndexTitle: "共享软件文件索引",
      publicFileIndexDescription: "复用资源管理器的现有索引，不会因查询重新扫盘。",
      publicDatabaseServiceTitle: "SQLite 数据库服务",
      publicDatabaseServiceDescription: "为本机程序提供相互隔离的命名数据库、参数查询和批处理事务。",
      publicAiModelCatalogTitle: "统一 AI 模型库",
      publicAiModelCatalogDescription: "由资源管理器统一管理模型目录和调度，LM Studio 负责运行模型。",
      lmStudioEndpointTitle: "LM Studio 地址",
      lmStudioEndpointDescription: "只接受本机 HTTP 地址。调用方始终使用资源管理器公共接口。",
      lmStudioAutoStartTitle: "按需启动 LM Studio",
      lmStudioAutoStartDescription: "有软件请求模型且 LM Studio 未运行时自动启动；不会自动加载模型。",
      aiGatewayTitle: "本地 AI 兼容密钥",
      aiGatewayDescription: "为不直接支持 LM Studio 的软件生成 OpenAI 或 Anthropic 兼容密钥；请求只会发送给本机运行的开源模型。",
      aiGatewayOpenAiProfile: "OpenAI 兼容",
      aiGatewayAnthropicProfile: "Anthropic 兼容",
      aiGatewayNamePlaceholder: "用途名称（可选）",
      aiGatewayGenerate: "生成密钥",
      aiGatewayGenerating: "处理中",
      aiGatewayOneTimeTitle: "密钥已生成",
      aiGatewayOneTimeDescription: "密钥只显示一次，请立即保存。之后无法再次查看。",
      aiGatewayApiKeyLabel: "API Key",
      aiGatewayBaseUrlLabel: "Base URL",
      aiGatewayCopy: "复制",
      aiGatewayRevoke: "撤销密钥",
      aiGatewayEmpty: "尚未生成兼容密钥。",
      aiGatewayLoadFailed: "读取本地 AI 密钥失败"
  },
  debug: {
    debugModeTitle: "调试模式",
    debugModeDescription: "显示并启用本机诊断选项。仅在排查问题时开启。",
    debugLogTitle: "保存调试日志",
    debugLogDescription: "记录排查问题所需的信息，可能增加少量磁盘和处理器占用。",
    hostManagerSmartCoordinatorScoreOnlyTitle: "仅分析，不执行优化",
    hostManagerSmartCoordinatorScoreOnlyDescription: "继续监控并生成调度结果，但不实际更改软件或硬件状态。",
    hostManagerSmartCoordinatorPerformanceLogTitle: "记录调度性能",
    hostManagerSmartCoordinatorPerformanceLogDescription: "记录调度耗时和负载，用于排查资源管理器自身开销。"
  },
  credits: {
    heroTitle: "许可与鸣谢",
    heroBody: "衷心感谢 Microsoft，以及 Windows、.NET、WebView2 和整个开发者社区。没有这些作者和维护者多年积累的工具、接口与基础组件，一个个人开发者不可能独自完成资源管理器。无论许可是否要求署名，我们都珍视并感谢每一份贡献。本项目独立开发，鸣谢不代表上游为本应用签名、认证或背书。",
    dependencyListTitle: "完整依赖鸣谢",
    dependencyListBody: "感谢以下所有包的作者与维护者。名单来自前端锁文件和实际解析的 .NET 包，含构建、测试和可选依赖；列出不代表当前机器加载或分发了所有包。",
    groups: {
      project: "项目与贡献",
      runtime: "界面与运行组件",
      windows: "Windows 与诊断接口",
      data: "离线数据",
      build: "构建工具",
      hardware: "硬件监控与可选支持"
    },
    roles: {
      project: "本项目",
      frontendFramework: "前端 UI 框架",
      icons: "界面图标",
      serialization: "Solid 的序列化依赖",
      webView: "桌面网页容器",
      database: "本地数据库",
      nativeCompiler: "原生核心编译器",
      softwareCatalog: "软件元数据来源",
      buildToolchain: "前端构建工具链",
      typeSystem: "前端类型系统",
      buildRuntime: "构建期运行时与类型定义",
      dotnetRuntime: "本地应用运行环境",
      hookLibrary: "Windows 兼容支持",
      startupInjectionLibrary: "软件启动兼容支持",
      traceLibrary: "系统性能分析",
      windowsManagement: "Windows 系统信息",
      nvidiaTelemetry: "NVIDIA GPU 遥测",
      nvidiaExtension: "NVIDIA GPU 扩展接口",
      amdTelemetry: "AMD GPU/iGPU 遥测",
      signedHardwareAccess: "签名硬件访问",
      hardwareBridge: "通用硬件传感桥",
      externalReference: "外部遥测参考 / 可选辅助",
      deviceIdDatabase: "硬件 ID 名称数据库"
    },
    notes: {
      windowsFoundation: "感谢 Microsoft 与 Windows 工程团队提供 Win32、Shell、COM、Direct3D/DXGI、PDH、ETW、服务、密码和网络接口。这些由操作系统提供，不是另行捆绑的软件。",
      microsoftTools: "特别感谢 .NET SDK、MSBuild、NuGet、PowerShell 的作者与维护者，为个人开发提供编译、发布、包管理和自动化工具。",
      managedServices: "感谢 Microsoft 与 .NET 贡献者提供服务生命周期集成与基础 API。TypeExtensions 在当前目标中仅为解析包占位，不代表额外 DLL。",
      nativeToolchain: "感谢 GCC 与 MinGW 的作者及维护者提供原生编译与运行库基础。部分原生组件静态链接 libgcc/libstdc++；具体分发许可另列，不因鸣谢省略核查。",
      buildContributors: "感谢全部构建依赖的作者与维护者，包括 Solid 编译与刷新工具、源码映射工具、浏览器数据及小型基础库。下方完整名单保留锁文件中的每个包和版本，包括可选平台依赖。",
      testContributors: "感谢 Playwright、xUnit.net、Coverlet 与 Microsoft 测试平台贡献者，使自动化交互、回归测试和覆盖率检查成为可能。它们用于开发验证，不是基础产品运行依赖。",
      upstreamContributors: "同样感谢 .NET 与 WebView2 原始第三方声明中列出的全部作者，包括 ANTLR、Unicode、Mono、Brotli、LLVM 等标准、运行库和算法贡献者。完整原文随包保留；这不是逐项运行时 DLL 清单。",
      resourceManager: "Copyright (c) 2026 BIOcanse。本项目采用 Apache-2.0；第三方组件保留各自的许可。",
      solid: "用于构建界面与交互。",
      lucide: "界面使用 lucide-solid；保留 Lucide 的 ISC 许可及 Feather 衍生图标的 MIT 许可与作者信息。",
      seroval: "Solid 的传递依赖；保留 Alexis Munsayac 的 MIT 许可与版权信息。",
      webView2: "桌面容器使用 WebView2 SDK；系统 WebView2 Evergreen Runtime 由 Microsoft 独立分发，按其许可使用。",
      sqlite: "用于本地数据存储。Microsoft.Data.Sqlite 为 MIT 许可，SQLitePCLRaw 3.x 为 Apache-2.0 许可；SourceGear 分发的 SQLite 核心由上游声明为公有领域。",
      zig: "用于编译原生核心；编译器不是用户运行程序的前置依赖。",
      softwareCatalog: "离线软件目录使用 WinGet 的 MIT 许可数据及 Wikidata 的 CC0 结构化数据；项目摘要和中文翻译为人工整理。",
      vite: "负责开发构建和 Solid 前端打包。",
      typescript: "用于提高界面代码和设置模型的可靠性。",
      node: "用于前端构建和开发工具。",
      dotnet: "提供 Resource Manager 的本地运行环境。",
      minHook: "感谢 Tsuda Kageyu、贡献者与 HDE 作者 Vyacheslav Patkov，提供挂钩和指令解码基础；完整原始许可证与署名随包保留。",
      detours: "Microsoft Research 开源项目，为软件启动兼容功能提供支持；项目保留原始许可证与版权信息。",
      traceEvent: "用于读取 Windows 系统性能信息。",
      systemManagement: "用于读取 Windows 系统与硬件信息。",
      nvml: "用于读取 NVIDIA 显卡频率、显存、功耗和温度。",
      nvapi: "用于补充 NVIDIA 显卡风扇、冷却和电气信息。",
      adlx: "用于读取 AMD 显卡占用、频率、功耗、温度和电压。",
      pawnIo: "官方签名驱动，AMD SMU 读数、Intel 处理器控制和主板与内存传感共用；不会静默安装。",
      pawnIoModules: "官方签名模块（LGPL-2.1）：RyzenSMU 和 IntelMSR 在点击验证时取得；LPC、EC、SMBus 访问模块嵌在 LibreHardwareMonitorLib 中。",
      libreHardwareMonitor: "随产品提供的官方核心库 0.9.6（MPL-2.0），只启用主板和内存两组，经 PawnIO 读取主板温度、电压、风扇和内存温度；不启动 GUI 或网页服务。",
      blacktempelToolkits: "LibreHardwareMonitorLib 依赖的磁盘信息、内存 SPD 读取和基础库（1.1.2、1.4.2、1.0.7），作者 Florian K.，MPL-2.0。",
      hidSharp: "LibreHardwareMonitorLib 依赖的 USB HID 设备访问库 2.6.4，作者 James F. Bellinger，Apache-2.0。",
      monoPosix: "LibreHardwareMonitorLib 依赖的 Mono POSIX 接口 1.0.0，由 Microsoft 发布，按 Mono 类库的 MIT 许可。",
      usbIds: "提供 USB VID/PID 到厂商和设备名称的本地离线解析；仅在设备详情打开时按需加载。",
      pciIds: "提供 PCI VEN/DEV/SUBSYS 到厂商、设备和子系统名称的本地离线解析；仅在设备详情打开时按需加载。"
    },
    linkLabels: {
      official: "官网",
      github: "GitHub",
      docs: "文档",
      license: "License",
      nuget: "NuGet",
      gpuOpen: "GPUOpen",
      eula: "EULA",
      runtime: "Runtime",
      aspnet: "ASP.NET Core",
      vite: "Vite",
      solidPlugin: "Solid 插件"
    }
  }
};

const enSettingsCopy: SettingsCopy = {
  updates: enUpdatesCopy,
  navigationLabel: "Settings sections",
  sections: {
    performance: "Performance",
    appearance: "Appearance",
    systemIntegration: "System Integration",
    updates: "Updates & Versions",
    debug: "Debug",
    credits: "Credits"
  },
  saveState: {
    saving: "Saving",
    saved: "Saved",
    constrained: "Saved; unavailable runtime options were constrained",
    partial: "Saved, but not fully applied",
    error: "Save failed",
    dirty: "Unsaved",
    conflict: "Settings changed elsewhere; reload before saving"
  },
  loadState: {
    loading: "Loading committed settings…",
    errorTitle: "Settings are unavailable",
    errorDescription: "Defaults are not being shown or used as if they were persisted settings. Restore the local service and retry.",
    staleTitle: "Settings are out of sync",
    staleDescription: "This copy may be stale. Changes will not be submitted until a reload succeeds."
  },
  actions: {
    save: "Save",
    restoreDefaults: "Restore Defaults",
    retry: "Retry",
    reload: "Reload",
    reapply: "Apply Again"
  },
  performance: {
    smartMonitoringTitle: "On-demand monitoring",
    smartMonitoringDescription: "Sampling now always follows visible demand. This old switch is disabled while its future use is undecided.",
    adaptiveBooleanModeOptions: [
      { id: "enabled", label: "On" },
      { id: "disabled", label: "Off" }
    ],
    preciseGpuPlacementTitle: "Precise GPU selection",
    preciseGpuPlacementDescription: "Allow precise GPU selection for supported render processes. Runtime switching still depends on process compatibility, and each app remains configurable.",
    preciseGpuPlacementModeOptions: {
      basic: { label: "Off", description: "Use only GPU preferences supported by Windows." },
      precise: { label: "On", description: "Allow an exact target GPU to be selected for software." }
    },
    automaticMemoryCleanupTitle: "Emergency cleanup thresholds",
    automaticMemoryCleanupDescription: "Background cleanup follows resource pressure levels. These thresholds set when low remaining physical memory or commit capacity triggers emergency cleanup. They apply only when the optimization mode includes memory scheduling.",
    physicalMemoryAutomaticCleanupLabel: "Physical memory free %",
    virtualMemoryAutomaticCleanupLabel: "Commit headroom %",
    memoryOptimizationTargetTitle: "Memory Optimization Target",
    memoryOptimizationTargetDescription: "Usage above a target continues slow level-one release without triggering emergency cleanup.",
    physicalMemoryOptimizationTargetLabel: "Physical memory target usage %",
    virtualMemoryOptimizationTargetLabel: "Target commit usage %",
    gpuOverflowTitle: "Dedicated GPU overflow thresholds",
    gpuOverflowDescription: "When either threshold is reached, move low-scoring background processes from the dedicated GPU to the integrated GPU. Requires active scheduling and an available integrated GPU.",
    gpuOverflowUsageLabel: "Dedicated GPU usage %",
    gpuOverflowDedicatedMemoryLabel: "Dedicated GPU memory usage %",
    realtimeActionBudgetTitle: "Realtime action budget",
    realtimeActionBudgetDescription: "Limit realtime actions per round by a fixed count or a percentage of all processes identified in that round, rounded up. Switching modes keeps both values.",
    realtimeActionBudgetModeOptions: [{"id":"fixed","label":"Fixed count"},{"id":"percent","label":"Process percentage"}],
    realtimeActionBudgetCountLabel: "Fixed action count",
    realtimeActionBudgetPercentLabel: "All processes %",
    refreshCadenceTitle: "Information Update Frequency",
    refreshCadenceDescription: "Control how quickly monitoring information and status are updated. Faster updates use slightly more resources.",
    presetNumericModeOptions: [
      { id: "aotu", label: "Automatic", description: "Choose a refresh rate for the current performance mode and window state." },
      { id: "preset", label: "Lock Preset", description: "Always use the selected preset cadence." },
      { id: "custom", label: "Custom", description: "Always use the manually entered millisecond value." }
    ],
    refreshCadencePresetLabel: "Preset",
    refreshCadenceCustomLabel: "Custom (ms)",
    refreshCadencePresetOptions: [
      { id: "responsive", label: "Responsive", description: "Prioritizes foreground interaction with faster refreshes." },
      { id: "balanced", label: "Balanced", description: "A practical update rate for everyday use." },
      { id: "lowPower", label: "Low Power", description: "Low-power background strategy." },
      { id: "quiet", label: "Quiet", description: "Lowest cadence while keeping necessary refreshes." }
    ],
    refreshCadenceItems: {
      monitor: { label: "Monitor Snapshot / Bars", description: "Refreshes live dashboard values and resource bars." },
      resourceTable: { label: "Resource Table", description: "Refreshes the software and process resource list." },
      localSystem: { label: "Local System", description: "Refreshes low-frequency local status such as uptime." }
    }
  },
  appearance: {
    cacheDescription: "Appearance, refresh timing and overlay appearance are cached on this device. Clearing the cache restores defaults.",
    resetCache: "Reset interface preferences",
    uiStyleTitle: "Interface style",
    uiStyleDescription: "Changes the corners, lines, shadows and fonts of controls; the layout stays the same. Each style has its own palettes.",
    themeTitle: "Palette",
    themeDescription: "Choose one of the current style's palettes. System follows the Windows light or dark setting.",
    animationTitle: "Motion",
    animationDescription: "Choose full motion, no motion, or simplified visuals for lower interface overhead.",
    resourceBarFrameRateTitle: "Resource Bar Frame Rate",
    resourceBarFrameRateDescription: "How many frames per second resource-bar transitions draw. Follow display uses the monitor refresh rate; Custom caps the frames to lower the cost. Applies only with Normal motion.",
    resourceBarFrameRateModeOptions: [
      { id: "display", label: "Follow display", description: "Draw a frame on every screen refresh." },
      { id: "custom", label: "Custom", description: "Draw at most the set number of frames per second (10–240)." }
    ],
    resourceBarFrameRateCustomLabel: "Frame rate (FPS)",
    barColorTitle: "Bar Colours",
    barColorDescription: "Use fixed colours by software type or assign a different colour to each software item.",
    byteUnitTitle: "Capacity Units",
    byteUnitDescription: "1 GiB = 1024 MiB, 1 GB = 1000 MB. Switching changes the numbers themselves, not just the labels.",
    readingSourceTitle: "Reading Sources",
    readingSourceDescription: "How readings with more than one source are listed in the monitoring console and device details. By default only the most accurate source on this computer is listed; when all available sources are shown, the most accurate one is marked Preferred. Scheduling always uses the most accurate source and is not affected by this setting.",
    readingSourceModeOptions: [
      { id: "preferred", label: "Most accurate only", description: "List only the most accurate source of each reading on this computer." },
      { id: "all", label: "All available", description: "List every available source of each reading and mark the most accurate one." }
    ],
    fontSmoothingTitle: "Text Smoothing",
    fontSmoothingDescription: "Balance text clarity and interface cost automatically, or lock a rendering mode.",
    languageTitle: "Interface language",
    languageDescription: "Choose the language used by the Resource Manager interface. System uses the Windows display language.",
    settingsLanguageTitle: "Interface language",
    settingsLanguageDescription: "Changes the language of the whole interface, including the desktop shell tray and window title. System uses the Windows display language.",
    languageSelectLabel: "Language",
    uiStyleOptions: {
      standard: "Standard",
      soft: "Soft",
      outline: "Outline",
      terminal: "Terminal"
    },
    themeOptions: {
      system: "System",
      light: "Light",
      dark: "Dark",
      lowContrast: "Low contrast",
      paper: "Paper",
      graphite: "Graphite",
      blueprint: "Blueprint",
      phosphor: "Phosphor",
      amber: "Amber",
      ice: "Ice"
    },
    animationOptions: {
      auto: { label: "Automatic", description: "Use normal motion in the foreground and switch to the ultra-performance appearance in low-power background states" },
      normal: { label: "Normal motion", description: "Enable restrained global transitions" },
      none: { label: "No motion", description: "Disable all transitions and animations, keep the refined look" },
      ultra: { label: "Ultra performance", description: "Disable motion and flatten visuals (no shadows / highlights / blur) for the lowest overhead" }
    },
    barColorOptions: {
      type: { label: "Fixed by type", description: "Colour by system, adapted, and general software; adjacent segments use dividers" },
      distinct: { label: "Distinct colours", description: "A different colour per software, no type meaning, no dividers" }
    },
    byteUnitOptions: {
      native: {
        label: "Match the hardware",
        description: "GiB for memory, VRAM and caches (memory capacity is a power of two); GB for disk capacity, file sizes and transfer totals (matching what vendors print)"
      },
      binary: { label: "Always GiB", description: "Always divide by 1024; labels are always KiB / MiB / GiB" },
      decimal: { label: "Always GB", description: "Always divide by 1000; labels are always kB / MB / GB" }
    },
    fontSmoothingOptions: {
      auto: { label: "Automatic", description: "Use system ClearType normally and reduce smoothing cost in ultra-performance mode" },
      system: { label: "System ClearType", description: "Native Windows subpixel rendering, crispest text" },
      grayscale: { label: "Grayscale", description: "Grayscale anti-aliasing, softer, can suit some high-DPI screens" },
      disabled: { label: "Off", description: "Disable additional text smoothing and use direct glyph rasterization" }
    },
  },
    systemIntegration: {
      crashRecoveryTitle: "Restart after a crash",
    crashRecoveryDescription: "Automatically restart the background service if it crashes. Normal stops do not trigger a restart.",
    crashRestartDelayTitle: "Delay before restart (seconds)",
    crashRestartCountTitle: "Restarts allowed",
    crashCounterResetDescription: "Crash-free days before the counter resets: {days}. After the limit, start the service again manually.",
    crashCounterResetTitle: "Counter reset period (days)",
    crashRecoveryActualTitle: "Current configuration",
    crashRecoveryUnavailable: "Automatic recovery configuration is unavailable.",
    crashRecoveryApplied: "Automatic recovery configuration applied.",
    crashRecoverySkipped: "Automatic recovery is not applied in this run mode.",
    crashRecoveryFailed: "Automatic recovery configuration could not be applied.",
    crashRecoveryMismatch: "The current configuration differs from your settings.",
    crashRecoveryNonCrashWarning: "The current configuration also restarts after a normal stop with an error.",
    crashRecoveryWriteFailed: "Could not write the automatic recovery configuration.",
    crashRecoveryReadFailed: "Could not read the automatic recovery configuration.",
    taskManagerTitle: "Task Manager Shortcut Replacement",
      autoStartTitle: "Start with Windows",
      autoStartDescription: "Run the service at startup and stay in the tray after sign-in.",
      taskManagerDescription: "Take over Ctrl+Shift+Esc: when registered, Resource Manager starts if it is not running, or shows and brings the main window forward if it already exists; Ctrl+Alt+Del is not changed.",
      forceTerminateTitle: "Force Terminate Hotkey",
      forceTerminateDescription: "Terminate the focused foreground app and every app with a visible unresponsive window. Disabled by default.",
      forceTerminateWarning: "Triggering this hotkey immediately terminates processes and discards unsaved work. Enabling it requires Ctrl, Alt, or Win plus a non-modifier key.",
      hotkeyEmpty: "No keys configured",
      hotkeyAddKey: "Add key",
      hotkeyKeyLabel: "Key",
      hotkeyRemoveKey: "Remove key",
      hotkeyUnordered: "Press together",
      hotkeyOrdered: "Previous key first",
      publicServiceTitle: "Local Public Service",
      publicServiceDescription: "Allow local software to use shared functions provided by Resource Manager.",
      publicFileIndexTitle: "Shared Software File Index",
      publicFileIndexDescription: "Reuse Resource Manager's existing index without rescanning on queries.",
      publicDatabaseServiceTitle: "SQLite Database Service",
      publicDatabaseServiceDescription: "Provide isolated named databases, parameterized queries, and batch transactions to local programs.",
      publicAiModelCatalogTitle: "Unified AI Model Catalog",
      publicAiModelCatalogDescription: "Resource Manager manages the model catalog and scheduling while LM Studio runs the models.",
      lmStudioEndpointTitle: "LM Studio Endpoint",
      lmStudioEndpointDescription: "Only addresses on this computer are accepted. Other apps connect through Resource Manager.",
      lmStudioAutoStartTitle: "Start LM Studio On Demand",
      lmStudioAutoStartDescription: "Start LM Studio when software requests a model and it is not running; models are never loaded automatically.",
      aiGatewayTitle: "Local AI Compatibility Keys",
      aiGatewayDescription: "Generate OpenAI- or Anthropic-compatible keys for software without direct LM Studio support. Requests only go to open models running on this PC.",
      aiGatewayOpenAiProfile: "OpenAI Compatible",
      aiGatewayAnthropicProfile: "Anthropic Compatible",
      aiGatewayNamePlaceholder: "Purpose name (optional)",
      aiGatewayGenerate: "Generate Key",
      aiGatewayGenerating: "Working",
      aiGatewayOneTimeTitle: "Key Generated",
      aiGatewayOneTimeDescription: "The key is shown once. Save it now because it cannot be viewed again.",
      aiGatewayApiKeyLabel: "API Key",
      aiGatewayBaseUrlLabel: "Base URL",
      aiGatewayCopy: "Copy",
      aiGatewayRevoke: "Revoke Key",
      aiGatewayEmpty: "No compatibility keys have been generated.",
      aiGatewayLoadFailed: "Unable to load local AI keys"
  },
  debug: {
    debugModeTitle: "Debug Mode",
    debugModeDescription: "Show and enable local diagnostic options. Turn this on only while investigating a problem.",
    debugLogTitle: "Save Debug Logs",
    debugLogDescription: "Record information needed to investigate problems. This can use a small amount of disk and processor time.",
    hostManagerSmartCoordinatorScoreOnlyTitle: "Analyze Without Applying Changes",
    hostManagerSmartCoordinatorScoreOnlyDescription: "Continue monitoring and producing scheduling results without changing software or hardware state.",
    hostManagerSmartCoordinatorPerformanceLogTitle: "Record Scheduling Performance",
    hostManagerSmartCoordinatorPerformanceLogDescription: "Record scheduling duration and load to investigate Resource Manager's own overhead."
  },
  credits: {
    heroTitle: "Licenses and credits",
    heroBody: "My sincere thanks to Microsoft, the people behind Windows, .NET and WebView2, and the wider developer community. Their years of work provide foundations an individual developer could not build alone. Every contribution matters, whether or not attribution is required. Resource Manager is independently developed; these thanks do not imply upstream signing, certification or endorsement.",
    dependencyListTitle: "Complete dependency acknowledgements",
    dependencyListBody: "Thank you to every package author and maintainer below. This list covers the frontend lockfile and resolved .NET packages, including build, test and optional dependencies. Listing does not imply that every package is loaded or distributed on this machine.",
    groups: {
      project: "Project And Contribution",
      runtime: "Interface And Runtime Components",
      windows: "Windows And Diagnostics Interfaces",
      data: "Offline Data",
      build: "Build Tools",
      hardware: "Hardware Monitoring And Optional Support"
    },
    roles: {
      project: "Project",
      frontendFramework: "Frontend UI framework",
      icons: "Interface icons",
      serialization: "Solid serialization dependencies",
      webView: "Desktop web container",
      database: "Local database",
      nativeCompiler: "Native core compiler",
      softwareCatalog: "Software metadata sources",
      buildToolchain: "Frontend build toolchain",
      typeSystem: "Frontend type system",
      buildRuntime: "Build-time runtime and type definitions",
      dotnetRuntime: "Local application runtime",
      hookLibrary: "Windows compatibility support",
      startupInjectionLibrary: "Software startup compatibility",
      traceLibrary: "System performance analysis",
      windowsManagement: "Windows system information",
      nvidiaTelemetry: "NVIDIA GPU telemetry",
      nvidiaExtension: "NVIDIA GPU extension interface",
      amdTelemetry: "AMD GPU/iGPU telemetry",
      signedHardwareAccess: "Signed hardware access",
      hardwareBridge: "General hardware sensor bridge",
      externalReference: "External telemetry reference / optional helper",
      deviceIdDatabase: "Hardware ID name database"
    },
    notes: {
      windowsFoundation: "Thank you to Microsoft and the Windows engineering teams for Win32, Shell, COM, Direct3D/DXGI, PDH, ETW, services, cryptography and networking, supplied by the operating system.",
      microsoftTools: "Special thanks to the authors and maintainers of the .NET SDK, MSBuild, NuGet and PowerShell for compilation, publishing, package management and automation.",
      managedServices: "Thanks to Microsoft and .NET contributors for service lifetime integration and foundational APIs. TypeExtensions is a resolved placeholder for this target, not an extra DLL.",
      nativeToolchain: "Thanks to GCC and MinGW authors and maintainers for native compilation and runtime foundations. Some native components statically link libgcc/libstdc++; redistribution review remains separate.",
      buildContributors: "Thank you to every build dependency's authors and maintainers, including Solid compilation and refresh tools, source maps, browser data and small utility libraries. The full list retains every locked package and version, including optional platforms.",
      testContributors: "Thanks to Playwright, xUnit.net, Coverlet and Microsoft test-platform contributors for browser automation, regression tests and coverage. These are development tools, not base-product runtime dependencies.",
      upstreamContributors: "Our thanks extend to every author in the original .NET and WebView2 notices, including ANTLR, Unicode, Mono, Brotli and LLVM contributors. Full originals are retained; their contents are not a per-DLL runtime inventory.",
      resourceManager: "Copyright (c) 2026 BIOcanse. This project uses Apache-2.0. Third-party components retain their own licenses.",
      solid: "Powers the reactive component model used by the current lightweight Web shell frontend.",
      lucide: "The interface uses lucide-solid, retaining the Lucide ISC license and the MIT license and attribution for Feather-derived icons.",
      seroval: "Transitive Solid dependencies; the MIT license and Alexis Munsayac attribution are retained.",
      webView2: "The desktop container uses the WebView2 SDK. Microsoft distributes the system WebView2 Evergreen Runtime separately under its own terms.",
      sqlite: "Local data storage. Microsoft.Data.Sqlite uses MIT and SQLitePCLRaw 3.x uses Apache-2.0. Upstream dedicates the SQLite core distributed by SourceGear to the public domain.",
      zig: "Compiles the native core; users do not need the compiler to run the application.",
      softwareCatalog: "The offline catalog uses MIT-licensed WinGet metadata and CC0 Wikidata structured data. Summaries and Chinese translations are project-authored curation.",
      vite: "Handles development builds and Solid frontend packaging.",
      typescript: "Improves the reliability of interface code and settings models.",
      node: "Supports frontend builds and development tools.",
      dotnet: "Provides Resource Manager's local application runtime.",
      minHook: "Thanks to Tsuda Kageyu, contributors and HDE author Vyacheslav Patkov for hooks and instruction decoding. Complete original licenses and attribution are retained.",
      detours: "An open-source Microsoft Research project used for software startup compatibility. The original license and copyright notice are retained.",
      traceEvent: "Provides access to Windows system performance information.",
      systemManagement: "Provides access to Windows system and hardware information.",
      nvml: "Provides NVIDIA GPU clock, memory, power, and temperature information.",
      nvapi: "Adds NVIDIA GPU fan, cooling, and electrical information.",
      adlx: "Provides AMD GPU usage, clock, power, temperature, and voltage information.",
      pawnIo: "Officially signed driver shared by AMD SMU readings, Intel processor control, and the motherboard and memory sensors; never silently installed.",
      pawnIoModules: "Officially signed modules (LGPL-2.1). RyzenSMU and IntelMSR are fetched when you verify the component; the LPC, EC, and SMBus access modules are embedded in LibreHardwareMonitorLib.",
      libreHardwareMonitor: "Bundled official core library 0.9.6 (MPL-2.0) with only the motherboard and memory groups enabled, reading board temperatures, voltages, fans, and memory temperature through PawnIO; no GUI or web server is started.",
      blacktempelToolkits: "Disk information, memory SPD reading, and base libraries used by LibreHardwareMonitorLib (1.1.2, 1.4.2, 1.0.7), by Florian K., under MPL-2.0.",
      hidSharp: "USB HID device access library 2.6.4 used by LibreHardwareMonitorLib, by James F. Bellinger, under Apache-2.0.",
      monoPosix: "Mono POSIX interface 1.0.0 used by LibreHardwareMonitorLib, published by Microsoft under the MIT license of the Mono class libraries.",
      usbIds: "Provides local offline USB VID/PID vendor and device names; loaded only when device details are requested.",
      pciIds: "Provides local offline PCI VEN/DEV/SUBSYS vendor, device, and subsystem names; loaded only when device details are requested."
    },
    linkLabels: {
      official: "Website",
      github: "GitHub",
      docs: "Docs",
      license: "License",
      nuget: "NuGet",
      gpuOpen: "GPUOpen",
      eula: "EULA",
      runtime: "Runtime",
      aspnet: "ASP.NET Core",
      vite: "Vite",
      solidPlugin: "Solid plugin"
    }
  }
};

export function createSettingsLocale(language: ConcreteAppLanguageMode, patch?: SettingsCopyPatch): SettingsCopy {
  const base = language === "zh-CN" || language === "zh-TW" ? zhCnSettingsCopy : enSettingsCopy;
  return patch ? mergeSettingsCopy(base, patch) : base;
}

function mergeSettingsCopy(base: SettingsCopy, patch: SettingsCopyPatch): SettingsCopy {
  return mergeObject(base, patch) as SettingsCopy;
}

function mergeObject<T>(base: T, patch: SettingsCopyPatchObject<T> | T): T {
  if (!isMergeableObject(base) || !isMergeableObject(patch)) {
    return (patch ?? base) as T;
  }

  const result: Record<string, unknown> = { ...(base as Record<string, unknown>) };
  for (const [key, value] of Object.entries(patch as Record<string, unknown>)) {
    const current = result[key];
    result[key] = isMergeableObject(current) && isMergeableObject(value)
      ? mergeObject(current, value as Record<string, unknown>)
      : value;
  }

  return result as T;
}

function isMergeableObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

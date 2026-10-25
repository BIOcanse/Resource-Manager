export const zhStatusCopy = {
  status: {
    sentenceEnd: "。",
    actionFailed: "操作失败，请稍后重试",
    stateUpdated: "状态已更新",
    unknownState: "状态未知",
    noData: "暂无数据",
    unknownTime: "时间未知",
    systemGraphicsUsage: "系统图形占用",
    // 后端状态值统一映射到这些用户状态
    state: {
      payloadMissing: "已安装，运行文件缺失",
      updateAvailable: "有可用更新",
      available: "可用",
      installed: "已安装",
      installedUnverified: "已安装，等待确认",
      readyToInstall: "可安装",
      downloadable: "可下载",
      manualDownload: "需要手动下载",
      notInstalled: "未安装",
      running: "运行中",
      stopped: "已停止",
      starting: "正在启动",
      ready: "已就绪",
      refreshing: "正在刷新",
      warming: "正在准备",
      failed: "操作失败",
      unavailable: "暂时不可用",
      disabled: "已关闭",
      enabled: "已开启",
      queued: "等待中",
      canceling: "正在取消",
      canceled: "已取消",
      completed: "已完成",
      restored: "已恢复",
      missing: "未找到",
      unknown: "状态未知"
    },
    risk: {
      low: "低风险",
      medium: "中等风险",
      high: "高风险",
      root: "根目录迁移",
      blocked: "禁止迁移",
      unknown: "风险待确认"
    },
    migration: {
      kindRoot: "软件根目录",
      kindData: "软件数据",
      targetMisc: "其他数据",
      targetUser: "用户数据",
      classificationApplicationRoot: "软件根目录",
      classificationUserAppData: "用户应用数据",
      classificationProgramData: "共享应用数据",
      classificationDirectory: "普通目录",
      classificationFile: "文件",
      classificationMissing: "源位置不存在",
      classificationUnknown: "待确认数据",
      stateRunning: "监控中",
      stateStopped: "已停止",
      stateCompleted: "已完成",
      stateRestored: "已恢复",
      stateFailed: "未完成",
      stateUnknown: "状态未知"
    },
    metricGroup: {
      cpu: "处理器",
      gpu: "图形处理器",
      memory: "内存",
      virtualMemory: "虚拟内存",
      disk: "磁盘",
      network: "网络",
      motherboard: "主板",
      fan: "风扇",
      hardwareMonitor: "硬件传感器",
      system: "系统"
    },
    metricUnavailable: {
      needsComponent: "需要装上硬件支持组件",
      deviceNotProvided: "不支持读取"
    },
    component: {
      nameFallback: "硬件支持组件",
      purposeFallback: "为资源管理器补充硬件信息和相关功能。",
      names: {
          fanControlCore: "风扇控制核心",
          hardwareBridge: "硬件写入辅助进程",
        amdSmuPawnIo: "AMD 处理器传感支持",
        hardwareSensorsPawnIo: "硬件传感驱动（PawnIO）",
        intelMsrPawnIo: "Intel 处理器控制",
        libreHardwareMonitor: "主板与内存传感支持",
        sharedWebView2Runtime: "共享 WebView2 运行时",
        notebookOemFan: "笔记本厂商风扇支持",
        nvidiaNvml: "NVIDIA 显卡监控支持",
        nvidiaNvapi: "NVIDIA 显卡扩展监控支持",
        amdAdlx: "AMD 显卡监控支持",
      },
      purposes: {
          fanControlCore: "读取并控制机身风扇，执行固件或软件曲线。",
          hardwareBridge: "调整处理器功耗上限和电压，撤销时恢复保存的原值。",
        amdSmu: "直接读处理器的 SMU 数据表，提供 STAPM、功耗、电压、电流、温度和每核读数。",
        hardwareSensorsPawnIo: "安装官方签名的 PawnIO 驱动，供主板和内存传感使用；不会静默安装。",
        intelMsrPawnIo: "通过官方签名的 IntelMSR 模块调 PL1/PL2、时间窗和电压偏移；每次写入都回读核对，释放时恢复原值。",
        motherboardMemorySensors: "随产品提供的核心库，读主板温度、电压、风扇和内存温度；需先装硬件传感驱动。",
        nvidiaNvml: "从显卡驱动读频率、显存、功耗和温度。",
        nvidiaNvapi: "补上驱动基础接口没给的风扇、电压和电流。",
        amdGpu: "读 AMD 显卡的频率、温度、功耗和风扇。",
        notebookOemFan: "走厂商驱动读 CPU/GPU 风扇，认得出的机型更准。",
        sharedWebView2Runtime: "界面用的 Chromium 运行时，全机共享。"
      },
      categories: {
        hardwareMonitoring: "硬件监控"
      }
    },
    resourceData: {
    },
    detail: {
      status: "状态",
      description: "说明",
      software: "软件",
      unnamedSoftware: "未命名软件",
      content: "内容",
      savedLocation: "保存位置",
      createdAt: "创建时间",
      restoredAt: "恢复时间",
      migrationContent: "迁移内容",
      location: "位置",
      sourcePath: "原位置",
      destinationPath: "迁移后位置"
    }
  }
};

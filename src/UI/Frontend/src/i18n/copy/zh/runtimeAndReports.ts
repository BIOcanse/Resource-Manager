import { cjkText } from "../../cjkSpacing.ts";
export const zhRuntimeAndReportsCopy = {
  smartReport: {
    processTarget: (name: string, pid: number) => `${name}（进程 ${pid}）`,
    loading: "正在读取",
    unavailable: "状态不可用",
    refresh: "刷新",
    completed: "已完成",
    needsAttention: "需要关注",
    releasedMemory: "内存释放",
    releasedVram: "显存释放",
    empty: "暂无智能调度记录。",
    details: "详细信息",
    fallbackTarget: "调度",
    detailTitle: (target: string) => cjkText`${target}详细信息`,
    readFailed: "智能调度报告读取失败",
    showAllRecords: (count: number) => `显示全部 ${count} 条记录`,
    showRecentRecords: (count: number) => `只显示最近 ${count} 条`,
    section: {
      result: "调度结果",
      execution: "执行情况"
    },
    field: {
      target: "对象",
      change: "调整内容",
      affectedResource: "影响资源",
      state: "状态",
      updatedAt: "更新时间",
      accepted: "成功",
      timedOut: "超时",
      rejected: "未执行",
      releasedMemory: "已释放内存",
      releasedVram: "已释放显存"
    },
    itemCount: (count: number) => `${count} 项`,
    kind: {
      softwareResourceSettings: "软件资源设置",
      gpuSelection: "显卡选择",
      runtimeGpuSwitch: "运行时显卡切换",
      cpuCoreAllocation: "CPU 核心分配",
      resourceOptimization: "资源优化",
      schedulingAdjustment: "调度调整"
    },
    // 智能调度历史（docs/report_lifecycle_2026-10-28.md“智能调度记录”）。
    history: {
      title: "历史记录",
      loading: "正在读取历史记录",
      readFailed: "智能调度历史读取失败",
      empty: "暂无历史记录。",
      retentionOff: "保留智能调度历史已关闭，记录恢复后不再保留。可在“设置 → 运行策略”中开启。",
      loadMore: "加载更早的记录",
      field: {
        sequence: "序号",
        recordedAt: "记录时间",
        appliedAt: "应用时间",
        restoredAt: "恢复时间",
        restoreResult: "恢复结果"
      },
      restoreResult: {
        restored: "已恢复",
        targetExited: "目标已退出",
        ownershipLost: "已被其他程序修改，不再恢复",
        failed: "恢复失败",
        unknown: "结果未记录"
      }
    },
    resource: {
      cpu: "处理器",
      gpu: "图形处理器",
      memory: "内存",
      vram: "显存",
      disk: "磁盘",
      network: "网络",
      software: "软件资源"
    }
  },
  // 报告页的“清空历史”：一次删除这类报告的全部历史条目，活动条目保留（src/features/reportHistory）。
  reportHistory: {
    clear: "清空历史",
    clearing: "正在清空",
    confirmTitle: "清空历史条目？",
    confirm: "清空",
    confirmMessage: {
      optimizationReports: (count: string) => cjkText`将删除${count}条历史报告，活动报告保留。`,
      kernelLeaks: (count: string) => cjkText`将删除${count}条历史条目，活动条目保留。`,
      memoryProtection: (count: string) => cjkText`将删除${count}条内存保护处理记录。`,
      targetedRecordings: (count: string) => cjkText`将删除${count}份已完成的录制报告，正在进行的录制保留。`,
      smartRecords: "将删除全部智能调度历史记录，待恢复的记录保留。"
    },
    details: {
      optimizationReports: ["删除后无法恢复；同一问题以后再次出现时会重新记录。", "已信任的软件和设备仍保持信任。"],
      kernelLeaks: ["删除后无法恢复；同一问题以后再次出现时会重新记录。"],
      memoryProtection: ["删除后无法恢复。", "转储的切片文件不会删除，仍留在磁盘保留区所在的磁盘上，需要时自行处理。"],
      targetedRecordings: ["这些报告及其帧间隔和资源数据将被永久删除，无法恢复。"],
      smartRecords: ["删除后无法恢复；记录序号不会重新开始。"]
    },
    clearedTitle: "已清空历史",
    cleared: (count: string) => cjkText`已删除${count}条历史条目。`,
    failed: "清空历史失败。"
  },
  browserRuntime: {
    description: "查看 Web 界面运行环境和已安装的浏览器。",
    downloadShared: "下载共享运行时",
    refreshing: "正在刷新",
    refresh: "刷新",
    sharedRuntimeTitle: "当前共享运行时",
    sharedRuntimeDescription: "资源管理器与其它 WebView2 软件共用同一份系统运行时。",
    openOfficialSource: "打开官方来源",
    noSharedRuntime: "未检测到共享运行时",
    noSharedRuntimeDetail: "可以安装官方共享运行时；现有浏览器仍会列入备用目录。",
    inUse: "正在使用",
    installedBrowsersTitle: "已安装的浏览器",
    installedBrowsersDescription: "支持外部浏览器的软件可以使用这些浏览器。",
    itemCount: (count: number) => `${count} 项`,
    noBrowsers: "未发现可复用的浏览器。",
    currentFallback: "当前备用",
    fallbackRuntimeName: "运行时",
    detailTitle: (name: string) => cjkText`${name}详细信息`,
    version: (value: string) => `版本 ${value}`,
    unknown: "未知",
    detailsOf: (name: string) => cjkText`${name}详细信息`,
    sharedRuntimePurpose: "供本机 WebView2 软件共同使用的共享界面运行时。",
    externalBrowserPurpose: "供支持外部浏览器的软件打开 Web 界面；不能作为 WebView2 嵌入运行时。",
    chromiumReusePurpose: "供支持外部 Chromium 浏览器的软件复用。",
    runtimeInfoTitle: "运行时信息",
    field: {
      kind: "类型",
      version: "版本",
      state: "状态",
      source: "来源",
      runtimeDirectory: "运行目录",
      executable: "可执行文件"
    },
    available: "可用",
    kind: {
      webView2Runtime: "共享 WebView2 运行时",
      chromiumBrowser: "Chromium 浏览器",
      geckoBrowser: "Gecko 浏览器"
    },
    source: {
      systemMachine: "系统（全机）",
      systemUser: "系统（当前用户）",
      managed: "资源管理器下载",
      browserRegistration: "系统浏览器注册",
      knownInstall: "本机安装目录",
      local: "本机"
    }
  }
};
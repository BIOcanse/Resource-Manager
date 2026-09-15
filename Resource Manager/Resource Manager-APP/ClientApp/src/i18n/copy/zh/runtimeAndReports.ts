export const zhRuntimeAndReportsCopy = {
  smartReport: {
    panel: "智能调度报告",
    loading: "正在读取",
    updated: "智能调度状态已更新",
    unavailable: "状态不可用",
    refresh: "刷新",
    completed: "已完成",
    needsAttention: "需要关注",
    releasedMemory: "内存释放",
    releasedVram: "显存释放",
    empty: "暂无智能调度记录。",
    details: "详细信息",
    fallbackTarget: "调度",
    detailTitle: (target: string) => `${target}详细信息`,
    readFailed: "智能调度报告读取失败",
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
  browserRuntime: {
    title: "运行时管理",
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
    detailTitle: (name: string) => `${name}详细信息`,
    version: (value: string) => `版本 ${value}`,
    unknown: "未知",
    details: "详细信息",
    detailsOf: (name: string) => `${name}详细信息`,
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
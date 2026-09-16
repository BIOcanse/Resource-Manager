export const zhShellCopy = {
  common: {
    saving: "保存中",
    saveFailed: "保存失败"
  },
  feedback: {
    confirm: "确认",
    cancel: "取消",
    close: "关闭",
    success: "操作完成",
    error: "操作失败",
    warning: "需要处理",
    info: "提示"
  },
  page: {
    monitor: "监视控制台",
    components: "组件与软件",
    optimization: "性能优化",
    details: "详细信息",
    settings: "设置",
    diskUsage: "磁盘占用",
    control: "控制面"
  },
  control: {
    title: "控制面",
    intro: "风扇、显卡、处理器的调节都在这一页。控不了的也列出来，并说明差什么。",
    loadFailed: "读不到可控对象。",
    empty: "这台机器上还没认出可以调节的对象。",
    ready: "可调节",
    needsComponent: (name: string) => `需要 ${name}`,
    attachment: {
      integrated: "核显",
      discrete: "独显",
      unknown: "接法未知"
    },
    status: {
      unset: "未设定",
      edited: "已改动，未应用",
      applying: "正在应用",
      applied: "已应用",
      unsupported: "这台机器上控不了",
      failed: "没应用上"
    },
    kind: {
      gpu: "显卡",
      cpu: "处理器",
      fan: "风扇"
    }
  },
  diskUsage: {
    title: "磁盘占用",
    intro: "扫描后按真实大小把文件摆成方格图，一眼看出空间去哪了。",
    scopeLabel: "扫描范围",
    scopeAllVolumes: "全部磁盘",
    scopeVolume: "指定磁盘",
    scopeFolder: "指定文件夹",
    modeLabel: "扫描方式",
    modeFast: "快速扫描",
    modeFastHint: "读文件系统索引，整盘几秒扫完。只适用于 NTFS，且需要管理员权限。",
    modeFull: "全扫描",
    modeFullHint: "逐级遍历目录。慢，但任何磁盘都能扫。",
    chooseFolder: "选择文件夹",
    folderNotChosen: "还没选文件夹",
    scan: "开始扫描",
    rescan: "重新扫描",
    cancel: "取消扫描",
    scanning: "正在扫描",
    volumeColumnLabel: "磁盘",
    noVolumes: "没有找到可以扫描的磁盘。",
    notReady: "未就绪",
    freeOfTotal: (free: string, total: string) => `可用 ${free} / 共 ${total}`,
    volumeKind: {
      physical: "物理磁盘",
      virtual: "虚拟磁盘",
      removable: "可移动",
      network: "网络位置",
      optical: "光驱",
      unknown: "来源未知"
    },
    fastUnsupported: "这个磁盘没有可读的文件系统索引，快速扫描扫不到它；用全扫描。",
    skipped: "本次没扫到的目标",
    skipReason: {
      noFileSystemIndex: "文件系统没有可读的索引",
      needsElevation: "读索引需要管理员权限",
      volumeNotReady: "磁盘未就绪",
      targetUnavailable: "路径不存在或打不开"
    },
    scanKind: {
      masterFileTable: "读文件系统索引",
      directoryWalk: "逐级遍历目录"
    },
    navigateUp: "上一层",
    resetView: "复位视图",
    collapseSetup: "收起设置",
    expandSetup: "重新设置",
    scanTotals: (size: string, files: number, folders: number) =>
      `${size} · ${files} 个文件 · ${folders} 个文件夹`,
    fileCount: (count: number) => `${count} 个文件`,
    omitted: (count: number) =>
      `还有 ${count} 个方格这会儿太小或者不在视野里，放大就会出现。`,
    copied: "已复制",
    menuOpenLocation: "打开所在位置",
    menuProperties: "属性",
    menuCopyPath: "复制完整路径",
    menuDrillDown: "只看这一层",
    menuSize: "大小",
    menuPath: "路径",
    menuKindFile: "文件",
    menuKindDirectory: "文件夹",
    emptyTitle: "还没有扫描结果",
    emptyDetail: "选好范围和方式，点「开始扫描」。"
  },
  shell: {
    productName: "资源管理器",
    documentTitle: (page: string, product: string) => `${page} · ${product}`,
    pageNav: "页面切换",
    currentPage: "当前页面",
    runtimeCapability: "运行能力",
    taskCenter: "任务中心",
    taskCenterActive: (count: number) => `任务中心，${count} 项正在进行`,
    taskCenterSyncing: "任务中心 · 后端任务正在同步",
    taskCenterDisconnected: "任务中心 · 本机服务正在重新同步",
    taskCenterUnavailable: "任务中心 · 后端任务状态不可用",
    minimize: "最小化",
    maximize: "最大化",
    closeWindow: "关闭"
  }
};

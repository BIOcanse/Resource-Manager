export const zhSoftwareCopy = {
  softwareKind: {
    Adapted: "适配软件",
    Controlled: "受控软件",
    Unconfirmed: "未确认",
    Managed: "已管理",
    Game: "游戏",
    HighPerformance: "高性能软件",
    Other: "一般应用",
    WindowsSystem: "Windows 系统",
    WindowsComponent: "Windows 应用/组件",
    WindowsService: "Windows 服务",
    RuntimePackage: "Windows 应用",
    RuntimeProduct: "一般应用",
    RuntimeRoot: "一般应用",
    Unattributed: "未归属进程",
    Empty: "空余"
  },
  managementRole: {
    Dependency: "依赖",
    Support: "支持"
  },
  management: {
    categoryNav: "组件和软件分类",
    add: "添加",
    refresh: "刷新",
    refreshing: "刷新中",
    emptyCategory: "当前分类没有记录。",
    noSearchResults: (query: string) => `没有与“${query}”匹配的记录。`,
    clearSearch: "清除搜索",
    install: "安装",
    uninstall: "一键卸载",
    downloadAndInstall: "下载并安装",
    obtainInstaller: "获取安装器",
    installed: "已安装",
    settingsAndMigration: "设置和迁移",
    issueStrip: "软件问题",
    issueCurrentReport: "当前报告",
    issueKnownCatalog: "已知问题目录",
    issueReferences: (label: string) => `${label}参考资料`
  },
  managementPage: {
    browserRuntimeTab: "运行时管理",
    migrationTab: "迁移工作台",
    pageLabel: (kind: string) => `${kind}管理`,
    searchLabel: (kind: string) => `${kind}搜索`,
    search: "搜索",
    itemCount: (count: number) => `${count} 项`,
    countUnavailable: "--",
    softwareRegistrySupplement: "软件登记补充项",
    softwareRegistrySupplementDisabled: "当前启动配置仅显示组件目录，不加载软件登记补充项。",
    operationState: "操作状态",
    componentCatalog: "组件目录",
    softwareRegistry: "软件登记",
    viewOnly: "当前启动配置只提供查看。",
    detailsOf: (name: string) => `详细信息：${name}`,
    moreActionsOf: (name: string) => `更多操作：${name}`,
    currentSoftware: "当前软件",
    rootNeedsCheck: "根目录需要检查",
    details: "详细信息",
    moreActions: "更多操作"
  },
  manualSoftware: {
    addAdapted: "添加适配软件",
    addGame: "添加游戏",
    addHighPerformance: "添加高性能软件",
    addGeneral: "添加一般应用"
  }
};

import type { ManagementKind, ResourceTableViewMode } from "./types";

export const uiText = {
  common: {
    saving: "保存中",
    saveFailed: "保存失败"
  },
  feedback: {
    confirmTitle: "确认操作",
    confirm: "确认",
    cancel: "取消",
    close: "关闭",
    success: "操作完成",
    error: "操作失败",
    warning: "需要处理",
    info: "提示"
  },
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
    install: "一键安装",
    uninstall: "一键卸载",
    installed: "已安装",
    settingsAndMigration: "设置和迁移"
  },
  manualSoftware: {
    addAdapted: "添加适配软件",
    addGame: "添加游戏",
    addHighPerformance: "添加高性能软件",
    addGeneral: "添加一般应用"
  },
  resourceBreakdown: {
    panel: "软件资源占用",
    save: "保存",
    edit: "编辑",
    statusFallback: "暂无数据",
    scaleCapacity: "含空余",
    scaleActive: "现有占用",
    selectionLabel: (metric: string) => `${metric} 软件占用选择`,
    empty: "空余",
    emptyPercent: "空余占比",
    systemPercent: "系统占比",
    categoryCount: (count: number) => `${count} 个分类`,
    processCount: (count: number) => `${count} 个进程`,
    noAttributableProcess: "无可归因进程",
    noPidCategory: "无 PID 分类",
    providerNotSplit: "系统共享占用，无法归到单个进程。",
    etwSupplement: "已识别到进程，但尚未归到具体软件。",
    softwareInnerPercent: "软件内占比",
    gpuUsageLabel: (index: string) => `GPU${index} 占用率`,
    gpuVramLabel: (index: string) => `GPU${index} 显存占用`
  },
  resourceTable: {
    panel: "资源列表",
    performance: "性能",
    searchPlaceholder: "搜索名称、PID、状态",
    viewLabel: "资源列表视图",
    save: "保存",
    edit: "编辑",
    expandProcesses: "展开进程",
    collapseProcesses: "收起进程",
    modes: {
      software: "软件级",
      process: "进程级",
      performance: "性能"
    } satisfies Record<ResourceTableViewMode, string>,
    columns: {
      name: "名称",
      pid: "PID",
      status: "状态",
      user: "用户",
      architecture: "架构",
      cpu: "CPU",
      memory: "内存",
      disk: "磁盘",
      network: "网络"
    },
    gpuUsageLabel: (index: string) => `GPU${index} 占用率`,
    gpuVramLabel: (index: string) => `GPU${index} 显存占用`
  }
} as const;

export function softwareKindLabel(kind: string | null | undefined) {
  const key = (kind ?? "") as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[key] ?? uiText.softwareKind.Other;
}

export function softwareDisplayKindLabel(kind: string | null | undefined, displayKind?: string | null) {
  const kindLabel = softwareKindLabel(kind);
  const displayText = displayKind?.trim();
  if (displayText === "其他软件" || displayText === "其他应用" || displayText === "运行中软件") {
    return uiText.softwareKind.Other;
  }

  return displayText || kindLabel;
}

export function managementKindLabel(kind: ManagementKind) {
  return kind === "Dependency" || kind === "Support"
    ? uiText.managementRole[kind]
    : uiText.softwareKind[kind];
}

export function managementRoleLabel(role: string | null | undefined) {
  return role === "Support"
    ? uiText.managementRole.Support
    : uiText.managementRole.Dependency;
}

export {
  isRightToLeftLanguage,
  languageOptions,
  normalizeLanguageMode,
  resolveLanguageMode
} from "./i18n/settingsLanguages";
export { fallbackSettingsText, loadSettingsText, loadingSettingsText } from "./i18n/settingsLoader";
export type { CreditGroup, LanguageOption, SettingsTextBundle } from "./i18n/settingsTypes";

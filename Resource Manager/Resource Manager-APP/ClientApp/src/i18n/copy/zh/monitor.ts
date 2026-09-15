import type { ResourceTableViewMode } from "../../../types.ts";

export const zhMonitorCopy = {
  monitorPage: {
    addCard: "添加卡片",
    addCardLabel: "添加监控卡片",
    cancel: "取消",
    cancelEditLabel: "取消监控面板布局编辑",
    save: "保存",
    edit: "编辑",
    saveLayoutLabel: "保存监控面板布局",
    editLayoutLabel: "编辑监控面板布局",
    layoutEditUnavailable: "布局编辑暂不可用",
    layoutEditor: "监控面板布局编辑器",
    dashboardQuarantined: "仪表盘配置已损坏并隔离，当前使用明确保存的安全默认配置。",
    dashboardRecovered: "仪表盘配置已从最近一次有效副本恢复。",
    metricCatalog: "指标目录",
    liveMetrics: "实时指标",
    resourceUsage: "资源占用",
    resourceList: "资源列表"
  },
  dashboard: {
    panel: "监控面板",
    emptyMetric: "空指标",
    primarySlot: (ordinal: number) => `第 ${ordinal} 张卡片主指标`,
    detailSlot: (ordinal: number, index: number) => `第 ${ordinal} 张卡片明细指标 ${index}`,
    addMetric: (slot: string) => `为${slot}添加指标`,
    replaceMetric: (slot: string, metric: string) => `更换${slot}：${metric}`,
    removeMetric: (slot: string, metric: string) => `删除${slot}：${metric}`,
    catalogUnavailable: "指标目录暂不可用",
    metricUnavailable: "指标不可用"
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
    } as Record<ResourceTableViewMode, string>,
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
};

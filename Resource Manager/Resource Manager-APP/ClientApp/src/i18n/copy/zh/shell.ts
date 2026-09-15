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
    settings: "设置"
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

import type { AppCopy } from "../zh/index.ts";

export const enShellCopy: Pick<AppCopy, "common" | "feedback" | "page" | "shell"> = {
  common: {
    saving: "Saving",
    saveFailed: "Save failed"
  },
  feedback: {
    confirm: "Confirm",
    cancel: "Cancel",
    close: "Close",
    success: "Action completed",
    error: "Action failed",
    warning: "Needs attention",
    info: "Notice"
  },
  page: {
    monitor: "Monitoring Console",
    components: "Components and Software",
    optimization: "Performance Optimization",
    details: "Detailed Information",
    settings: "Settings"
  },
  shell: {
    productName: "Resource Manager",
    documentTitle: (page: string, product: string) => `${page} · ${product}`,
    pageNav: "Page navigation",
    currentPage: "Current page",
    runtimeCapability: "Runtime capability",
    taskCenter: "Task center",
    taskCenterActive: (count: number) => `Task center, ${count} in progress`,
    taskCenterSyncing: "Task center · backend tasks are syncing",
    taskCenterDisconnected: "Task center · the local service is resyncing",
    taskCenterUnavailable: "Task center · backend task state unavailable",
    minimize: "Minimize",
    maximize: "Maximize",
    closeWindow: "Close"
  }
};

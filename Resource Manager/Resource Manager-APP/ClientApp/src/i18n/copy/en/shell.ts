import type { AppCopy } from "../zh/index.ts";

export const enShellCopy: Pick<AppCopy, "common" | "feedback" | "page" | "control" | "diskUsage" | "shell"> = {
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
    settings: "Settings",
    diskUsage: "Disk Usage",
    control: "Control"
  },
  control: {
    title: "Control",
    intro: "Fan, graphics and processor tuning all live here. Whatever cannot be tuned is listed too, with the reason.",
    loadFailed: "Could not read the controllable devices.",
    empty: "Nothing tunable has been identified on this machine yet.",
    ready: "Adjustable",
    needsComponent: (name: string) => `Needs ${name}`,
    kind: {
      gpu: "Graphics",
      cpu: "Processor",
      fan: "Fans"
    }
  },
  diskUsage: {
    title: "Disk Usage",
    intro: "Scan, then see files laid out as tiles sized by what they actually take up.",
    scopeLabel: "Scan scope",
    scopeAllVolumes: "All drives",
    scopeVolume: "One drive",
    scopeFolder: "One folder",
    modeLabel: "Scan method",
    modeFast: "Fast scan",
    modeFastHint: "Reads the filesystem index, so a whole drive takes seconds. NTFS only, and needs administrator rights.",
    modeFull: "Full scan",
    modeFullHint: "Walks the directories one level at a time. Slow, but works on any drive.",
    chooseFolder: "Choose folder",
    folderNotChosen: "No folder chosen yet",
    scan: "Start scan",
    rescan: "Scan again",
    cancel: "Cancel scan",
    scanning: "Scanning",
    volumeColumnLabel: "Drive",
    noVolumes: "No scannable drive was found.",
    notReady: "Not ready",
    freeOfTotal: (free: string, total: string) => `${free} free of ${total}`,
    volumeKind: {
      physical: "Physical drive",
      virtual: "Virtual drive",
      removable: "Removable",
      network: "Network location",
      optical: "Optical drive",
      unknown: "Unknown source"
    },
    fastUnsupported: "This drive has no readable filesystem index, so a fast scan cannot reach it. Use a full scan.",
    skipped: "Not covered by this scan",
    skipReason: {
      noFileSystemIndex: "The filesystem has no readable index",
      needsElevation: "Reading the index needs administrator rights",
      volumeNotReady: "The drive is not ready",
      targetUnavailable: "The path does not exist or cannot be opened"
    },
    scanKind: {
      masterFileTable: "Read the filesystem index",
      directoryWalk: "Walked the directories"
    },
    navigateUp: "Up one level",
    resetView: "Reset view",
    collapseSetup: "Hide options",
    expandSetup: "Scan options",
    scanTotals: (size: string, files: number, folders: number) =>
      `${size} · ${files} files · ${folders} folders`,
    fileCount: (count: number) => `${count} files`,
    omitted: (count: number) =>
      `${count} more tiles are too small or off-screen right now. Zoom in to see them.`,
    copied: "Copied",
    menuOpenLocation: "Open file location",
    menuProperties: "Properties",
    menuCopyPath: "Copy full path",
    menuDrillDown: "Zoom into this",
    menuSize: "Size",
    menuPath: "Path",
    menuKindFile: "File",
    menuKindDirectory: "Folder",
    emptyTitle: "Nothing scanned yet",
    emptyDetail: "Pick a scope and a method, then start the scan."
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

import type { UpdatesCopy } from "./settingsTypes.ts";

export const loadingUpdatesCopy: UpdatesCopy = {
  autoUpdateTitle: "", autoUpdateDescription: "", installedVersionTitle: "", unknownVersion: "",
  checking: "", checkForUpdates: "", catalogFailed: "", historyIncomplete: "", historyStale: "",
  versionsTitle: "", versionsDescription: "", lastChecked: "", selectedTarget: "", prepare: "",
  confirmPrepare: () => "", prepareFailed: "", waitingForExit: "", stageLabel: "", unknownStage: "",
  stages: {}, otherVersions: "", installedUnknown: "", stable: "", preview: "", noLatest: "",
  noStable: "", latest: "", latestStable: "", verified: "", unavailableReasons: {}
};

export const enUpdatesCopy: UpdatesCopy = {
  autoUpdateTitle: "Automatic updates",
  autoUpdateDescription: "Save the automatic update preference. Automatic installation waits for trusted release signing; manual updates are available.",
  installedVersionTitle: "Installed version",
  unknownVersion: "Unknown",
  checking: "Checking…",
  checkForUpdates: "Check for updates",
  catalogFailed: "Unable to read release catalog: ",
  historyIncomplete: "Release history is incomplete.",
  historyStale: "Showing the previous release catalog; reconnect and retry.",
  versionsTitle: "Product versions",
  versionsDescription: "Newest first; each series shows its newest release by default.",
  lastChecked: "Last checked: ",
  selectedTarget: "Selected target: ",
  prepare: "Prepare and install selected update",
  confirmPrepare: (version) => `Update to ${version}? The updater will verify the package, wait for all desktop sessions to exit, stop the service, preserve and migrate data, and check the new version. It restores the previous version on failure.`,
  prepareFailed: "Unable to prepare update.",
  waitingForExit: "Update ready. Exit every Resource Manager desktop session from the tray to apply it.",
  stageLabel: "Update status",
  unknownStage: "Unknown update state",
  stages: { idle: "Idle", checking: "Checking for updates", downloading: "Downloading package", verifying: "Verifying package", waitingForExit: "Waiting for desktop sessions to exit", completed: "Update completed", error: "Update failed" },
  otherVersions: "Other versions",
  installedUnknown: "Installed version is unknown; upgrade order cannot be checked.",
  stable: "Stable",
  preview: "Preview",
  noLatest: "No latest release is available.",
  noStable: "No stable release is available.",
  latest: "Latest release",
  latestStable: "Latest stable release",
  verified: "Verified release",
  unavailableReasons: {
    "无法确认当前安装版本，升级选择已关闭。": "Installed version is unknown; upgrade selection is disabled.",
    "只能选择高于当前安装版本的发行版。": "Only releases newer than the installed version can be selected."
  }
};

export const zhCnUpdatesCopy: UpdatesCopy = {
  autoUpdateTitle: "自动更新",
  autoUpdateDescription: "保存自动更新偏好。可信发布签名接入前不会自动安装；手动更新可用。",
  installedVersionTitle: "当前安装版本",
  unknownVersion: "无法识别",
  checking: "检查中…",
  checkForUpdates: "检查更新",
  catalogFailed: "无法读取版本目录：",
  historyIncomplete: "版本历史尚未完整读取。",
  historyStale: "正在显示上次读取的版本目录；请联网后重试。",
  versionsTitle: "产品版本",
  versionsDescription: "从新到旧排列；每个系列默认显示最新一版。",
  lastChecked: "上次检查：",
  selectedTarget: "已选择目标版本：",
  prepare: "准备并安装所选更新",
  confirmPrepare: (version) => `将更新到 ${version}。更新器会校验发行包，等待你退出所有桌面界面，停止服务，保留并迁移数据，验证新版本；失败时恢复旧版。继续准备吗？`,
  prepareFailed: "无法准备更新。",
  waitingForExit: "更新包已准备。请从托盘退出所有 Resource Manager 桌面界面；更新器会随后切换版本。",
  stageLabel: "更新状态",
  unknownStage: "未知更新状态",
  stages: { idle: "空闲", checking: "检查更新", downloading: "下载发行包", verifying: "校验发行包", waitingForExit: "等待桌面界面退出", completed: "更新完成", error: "更新失败" },
  otherVersions: "其他版本",
  installedUnknown: "无法确认已安装版本，不能判断升级方向。",
  stable: "稳定版",
  preview: "预览版",
  noLatest: "暂无最新版本。",
  noStable: "暂无稳定版本。",
  latest: "最新版本",
  latestStable: "最新稳定版本",
  verified: "已验证版本",
  unavailableReasons: {
    "无法确认当前安装版本，升级选择已关闭。": "无法确认当前安装版本，升级选择已关闭。",
    "只能选择高于当前安装版本的发行版。": "只能选择高于当前安装版本的发行版。"
  }
};

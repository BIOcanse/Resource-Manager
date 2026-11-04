import { operationsFeedback } from "../operationsFeedback.ts";
export const managementFeedback = {
  ...operationsFeedback["zh-CN"],
  "resultCode": "结果代码",
  "removeManual": "移除手动登记",
  "confirmManual": "仅移除此手动登记及分类，不会卸载或删除软件文件。继续？",
  "manualRemoved": "已移除手动登记。",
  "manualRemoveFailed": "移除手动登记失败。",
  "metadataFailed": "软件资料加载失败。",
  "metadataStale": "软件资料更新失败，当前内容可能已过时或使用上一种语言。",
  "metadataLoading": "正在加载软件资料…",
  "retry": "重试",
  "networkFailure": "下载请求失败，请检查网络后重试。",
  "fileUnavailable": "所需文件不可用。",
  "accessDenied": "访问被拒绝，请检查权限。",
  "requestRejected": "操作请求被拒绝，请检查操作详情。",
  "ioFailure": "读写文件失败，请检查路径及可用空间。",
  "unexpectedFailure": "操作遇到意外错误。",
  "noExecutionEvidence": "没有动作执行证据，可以重试。",
  "outcomeUncertain": "动作可能已经执行，请刷新核对后再操作。",
  "managedRootRemoved": "已清理该依赖的受管安装根目录。驱动自带或系统全局运行库不会被卸载。",
  "uninstallerLaunchRequested": "已请求启动 Windows 卸载器。完成后请刷新软件清单。"
};

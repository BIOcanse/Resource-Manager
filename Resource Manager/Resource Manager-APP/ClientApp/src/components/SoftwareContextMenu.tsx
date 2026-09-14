import type { StandardContextMenuModel } from "./StandardContextMenu";
import type { SystemProcessIdentity } from "../types";
import { normalizeSystemProcessIdentities } from "../processes/systemProcessIdentity";

export interface SoftwareContextMenuTarget {
  name: string;
  softwareId?: string | null;
  softwareName?: string | null;
  processIds?: number[];
  processTargets?: SystemProcessIdentity[];
  processNames?: string[];
  executablePaths?: string[];
  rootPaths?: string[];
  canExpand?: boolean;
  expanded?: boolean;
}

export interface SoftwareContextMenuActions {
  allowSystemActions: boolean;
  onExpand: (target: SoftwareContextMenuTarget) => void;
  onTerminate: (target: SoftwareContextMenuTarget) => void;
  onCreateDump: (target: SoftwareContextMenuTarget) => void;
  onGoToDetails: (target: SoftwareContextMenuTarget) => void;
  onOpenFileLocation: (target: SoftwareContextMenuTarget) => void;
  onSearchOnline: (target: SoftwareContextMenuTarget) => void;
  onProperties: (target: SoftwareContextMenuTarget) => void;
}

export function createSoftwareContextMenuModel(
  x: number,
  y: number,
  target: SoftwareContextMenuTarget,
  actions: SoftwareContextMenuActions,
  returnFocusTarget?: HTMLElement | null): StandardContextMenuModel
{
  const hasProcess = normalizedProcessTargets(target).length > 0;
  const fileLocation = primaryFileLocation(target);
  const canGoToDetails = Boolean(target.softwareId);
  const systemActionUnavailableTitle = actions.allowSystemActions
    ? undefined
    : "当前启动配置只提供查看，不执行系统操作。";
  return {
    id: crypto.randomUUID(),
    x,
    y,
    returnFocusTarget,
    unavailableReason: actions.allowSystemActions
      ? "当前条目没有可展开、定位或执行的操作。"
      : "当前启动配置只提供查看，并且该条目没有可用的展开或详情操作。",
    items: [
      {
        id: "expand",
        label: target.expanded ? "折叠" : "展开",
        disabled: !target.canExpand,
        title: target.canExpand ? undefined : "当前列表没有可展开的进程组。",
        onSelect: () => actions.onExpand(target)
      },
      {
        id: "terminate",
        label: "结束任务",
        danger: true,
        disabled: !actions.allowSystemActions || !hasProcess,
        title: systemActionUnavailableTitle ?? (hasProcess ? undefined : "没有匹配到正在运行的进程。"),
        onSelect: () => actions.onTerminate(target)
      },
      {
        id: "dump",
        label: "创建内存转储文件",
        separatorBefore: true,
        disabled: !actions.allowSystemActions || !hasProcess,
        title: systemActionUnavailableTitle ?? (hasProcess ? undefined : "没有可创建转储的进程。"),
        onSelect: () => actions.onCreateDump(target)
      },
      {
        id: "details",
        label: "转到详细信息",
        separatorBefore: true,
        disabled: !canGoToDetails,
        title: canGoToDetails ? undefined : "当前软件没有稳定的软件标识，无法标记进程。",
        onSelect: () => actions.onGoToDetails(target)
      },
      {
        id: "file-location",
        label: "打开文件所在的位置",
        disabled: !actions.allowSystemActions || !fileLocation,
        title: systemActionUnavailableTitle ?? (fileLocation ? undefined : "没有可打开的安装目录或进程路径。"),
        onSelect: () => actions.onOpenFileLocation(target)
      },
      {
        id: "search",
        label: "在线搜索",
        disabled: !actions.allowSystemActions || !target.name.trim(),
        title: systemActionUnavailableTitle,
        onSelect: () => actions.onSearchOnline(target)
      },
      {
        id: "properties",
        label: "属性",
        disabled: !actions.allowSystemActions || !primaryPropertiesPath(target),
        title: systemActionUnavailableTitle ?? (primaryPropertiesPath(target) ? undefined : "没有可打开系统属性的文件或目录路径。"),
        onSelect: () => actions.onProperties(target)
      }
    ]
  };
}

export function normalizedProcessTargets(
  target: SoftwareContextMenuTarget
): SystemProcessIdentity[] {
  return normalizeSystemProcessIdentities(target.processTargets);
}

export function primaryFileLocation(target: SoftwareContextMenuTarget) {
  return firstText(target.executablePaths) ?? firstText(target.rootPaths);
}

export function primaryPropertiesPath(target: SoftwareContextMenuTarget) {
  return firstText(target.executablePaths) ?? firstText(target.rootPaths);
}

export function fileLocationSelectMode(target: SoftwareContextMenuTarget) {
  return Boolean(firstText(target.executablePaths));
}

function firstText(values?: string[]) {
  return (values ?? []).map((value) => value?.trim()).find(Boolean) ?? null;
}

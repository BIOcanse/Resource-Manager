import type { StandardContextMenuModel } from "./StandardContextMenu";
import type { SystemProcessIdentity } from "../types";
import { normalizeSystemProcessIdentities } from "../processes/systemProcessIdentity";
import { uiText } from "../text.ts";

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
    : uiText.contextMenu.viewOnly;
  return {
    id: crypto.randomUUID(),
    x,
    y,
    returnFocusTarget,
    unavailableReason: actions.allowSystemActions
      ? uiText.contextMenu.nothingToDo
      : uiText.contextMenu.viewOnlyNothingToDo,
    items: [
      {
        id: "expand",
        label: target.expanded ? uiText.contextMenu.collapse : uiText.contextMenu.expand,
        disabled: !target.canExpand,
        title: target.canExpand ? undefined : uiText.contextMenu.noExpandableGroup,
        onSelect: () => actions.onExpand(target)
      },
      {
        id: "terminate",
        label: uiText.contextMenu.endTask,
        danger: true,
        disabled: !actions.allowSystemActions || !hasProcess,
        title: systemActionUnavailableTitle ?? (hasProcess ? undefined : uiText.contextMenu.noRunningProcess),
        onSelect: () => actions.onTerminate(target)
      },
      {
        id: "dump",
        label: uiText.contextMenu.createDump,
        separatorBefore: true,
        disabled: !actions.allowSystemActions || !hasProcess,
        title: systemActionUnavailableTitle ?? (hasProcess ? undefined : uiText.contextMenu.noDumpProcess),
        onSelect: () => actions.onCreateDump(target)
      },
      {
        id: "details",
        label: uiText.contextMenu.goToDetails,
        separatorBefore: true,
        disabled: !canGoToDetails,
        title: canGoToDetails ? undefined : uiText.contextMenu.noStableIdentity,
        onSelect: () => actions.onGoToDetails(target)
      },
      {
        id: "file-location",
        label: uiText.contextMenu.openFileLocation,
        disabled: !actions.allowSystemActions || !fileLocation,
        title: systemActionUnavailableTitle ?? (fileLocation ? undefined : uiText.contextMenu.noFileLocation),
        onSelect: () => actions.onOpenFileLocation(target)
      },
      {
        id: "search",
        label: uiText.contextMenu.onlineSearch,
        disabled: !actions.allowSystemActions || !target.name.trim(),
        title: systemActionUnavailableTitle,
        onSelect: () => actions.onSearchOnline(target)
      },
      {
        id: "properties",
        label: uiText.contextMenu.properties,
        disabled: !actions.allowSystemActions || !primaryPropertiesPath(target),
        title: systemActionUnavailableTitle ?? (primaryPropertiesPath(target) ? undefined : uiText.contextMenu.noPropertiesPath),
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

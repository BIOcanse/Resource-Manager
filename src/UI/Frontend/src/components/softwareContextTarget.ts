import type { SoftwareContextMenuTarget } from "./SoftwareContextMenu";
import type { ResourceTableRow } from "../types";
import { normalizeSystemProcessIdentities } from "../processes/systemProcessIdentity.ts";
import { normalizeName, textOrEmpty } from "../utils.ts";

/** A process row already owns an exact instance; only software rows expand a group. */
export function enrichSoftwareContextTarget(
  target: SoftwareContextMenuTarget,
  liveRows: ResourceTableRow[]
): SoftwareContextMenuTarget {
  if (target.kind === "process") return target;
  const matchingRows = liveRows
    .filter(row => row.kind === "software" || row.kind === "process")
    .filter(row => softwareContextTargetMatchesRow(target, row));
  const softwareRow = matchingRows.find(row => row.kind === "software");
  const processRows = matchingRows.filter(row => row.kind === "process");
  return {
    ...target,
    softwareId: target.softwareId ?? softwareRow?.softwareId,
    softwareName: target.softwareName ?? softwareRow?.softwareName ?? softwareRow?.name,
    processIds: [...new Set([
      ...(target.processIds ?? []),
      ...(softwareRow?.processIds ?? []),
      ...processRows.flatMap(row => row.processIds?.length ? row.processIds : row.processId ? [row.processId] : [])
    ].filter(value => Number.isFinite(value) && value > 0))],
    processTargets: normalizeSystemProcessIdentities([
      ...(target.processTargets ?? []),
      ...processRows.flatMap(row => row.processId && row.processStartKey
        ? [{ processId: row.processId, processStartKey: row.processStartKey }]
        : [])
    ]),
    processNames: uniqueStrings([
      ...(target.processNames ?? []),
      ...(softwareRow?.processNames ?? []),
      ...processRows.flatMap(row => row.processNames ?? [row.name])
    ]),
    executablePaths: uniqueStrings([
      ...(target.executablePaths ?? []),
      ...(softwareRow?.executablePaths ?? []),
      ...processRows.flatMap(row => row.executablePaths ?? [])
    ]),
    rootPaths: uniqueStrings(target.rootPaths ?? [])
  };
}

export function softwareContextTargetMatchesRow(
  target: Pick<SoftwareContextMenuTarget, "name"> & Partial<Pick<SoftwareContextMenuTarget, "softwareId" | "softwareName">>,
  row: { softwareId?: string | null; softwareName?: string | null; name: string }
) {
  if (target.softwareId && row.softwareId && target.softwareId.toLowerCase() === row.softwareId.toLowerCase()) return true;
  const targetNames = [target.softwareName, target.name].map(normalizeName).filter(Boolean);
  const rowNames = [row.softwareName, row.name].map(normalizeName).filter(Boolean);
  return targetNames.some(name => rowNames.some(rowName => rowName === name));
}

function uniqueStrings(values: string[]) {
  return [...new Set(values.map(textOrEmpty).filter(Boolean))];
}

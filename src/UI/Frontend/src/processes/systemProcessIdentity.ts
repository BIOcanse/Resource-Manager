import type { SystemProcessIdentity } from "../types";

export function normalizeSystemProcessIdentities(
  candidates: readonly SystemProcessIdentity[] | null | undefined
): SystemProcessIdentity[] {
  const keysByPid = new Map<number, Set<string>>();
  for (const candidate of candidates ?? []) {
    const rawStartKey = candidate.processStartKey?.trim();
    if (!Number.isSafeInteger(candidate.processId)
        || candidate.processId <= 0
        || !rawStartKey
        || !/^[1-9]\d*$/.test(rawStartKey)) {
      continue;
    }

    const startKey = BigInt(rawStartKey).toString();
    const keys = keysByPid.get(candidate.processId) ?? new Set<string>();
    keys.add(startKey);
    keysByPid.set(candidate.processId, keys);
  }

  return [...keysByPid.entries()]
    .filter(([, keys]) => keys.size === 1)
    .map(([processId, keys]) => ({
      processId,
      processStartKey: [...keys][0]!
    }))
    .sort((left, right) => left.processId - right.processId);
}

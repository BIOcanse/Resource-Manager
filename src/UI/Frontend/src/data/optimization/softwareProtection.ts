import type { ProtectedOptimizationTarget } from "../../types.ts";

/** Backend sources: three user sources followed by two default sources. */
export const protectionSources = ["software_detail", "report", "protection_list", "software_catalog", "system_default"] as const;
export type ProtectionSource = typeof protectionSources[number];

export function isKnownProtectionSource(source: string): source is ProtectionSource {
  return (protectionSources as readonly string[]).includes(source);
}

/** Resolve active software-specific entries before kind entries. Highest level wins, then lowest ordinal ID; level zero still overrides. Process-specific entries require process context and are excluded here. */
export function resolveSoftwareProtection(
  targets: readonly ProtectedOptimizationTarget[],
  softwareId: string,
  softwareKind: string | null
): ProtectedOptimizationTarget | null {
  let own: ProtectedOptimizationTarget | null = null;
  let kind: ProtectedOptimizationTarget | null = null;
  for (const target of targets) {
    if (target.state !== "active") continue;
    if (target.targetType === "process") continue;
    if (target.targetType === "software_kind") {
      if (softwareKind && sameKey(target.softwareKind, softwareKind)) kind = higher(kind, target);
      continue;
    }
    if (sameKey(target.softwareId, softwareId)) own = higher(own, target);
  }
  return own ?? kind;
}

function sameKey(left: string | null | undefined, right: string) {
  return typeof left === "string" && left.trim().length > 0 && left.toLowerCase() === right.toLowerCase();
}

function higher(current: ProtectedOptimizationTarget | null, next: ProtectedOptimizationTarget) {
  if (!current) return next;
  const currentLevel = normalizedLevel(current.protectionLevel);
  const nextLevel = normalizedLevel(next.protectionLevel);
  return nextLevel > currentLevel || (nextLevel === currentLevel && next.id < current.id) ? next : current;
}

/** Backend normalization: zero disables protection; other levels prevent freezing. */
export function normalizedLevel(level: number) {
  return level === 0 ? 0 : 1;
}

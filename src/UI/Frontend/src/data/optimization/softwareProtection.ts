import type { ProtectedOptimizationTarget } from "../../types.ts";

/** 保护条目的来源（后端 OptimizationProtectionSources）：前三个是用户的，后两个是默认。 */
export const protectionSources = ["software_detail", "report", "protection_list", "software_catalog", "system_default"] as const;
export type ProtectionSource = typeof protectionSources[number];

export function isKnownProtectionSource(source: string): source is ProtectionSource {
  return (protectionSources as readonly string[]).includes(source);
}

/**
 * 一个软件当前由哪个条目决定保护，按后端 OptimizationProtectionTable.Resolve 的软件这一层往下：
 * 先看这个软件编号的条目（目标类型既不是 process 也不是 software_kind，softwareId 相同），
 * 没有再看软件类型的条目（software_kind，softwareKind 相同）。只算 active 的条目；
 * 同一层有几个时取等级高的，等级相同取编号按序数比较较小的（与后端 Higher 一致）。
 * 决定的条目等级为 0 也算决定（用户把默认关掉了）。没有条目适用时返回 null。
 * 按进程名的条目和共用服务宿主的条目要看具体进程，软件详情这一层不参与。
 */
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

/** 后端 OptimizationProtectionLevels.Normalize：0 是不保护，其他都是不冻结。 */
export function normalizedLevel(level: number) {
  return level === 0 ? 0 : 1;
}

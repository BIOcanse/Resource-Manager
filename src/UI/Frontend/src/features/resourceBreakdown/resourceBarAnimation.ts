// The settings owner has already resolved Auto using focus and GPU scheduling.
export function canAnimateResourceBar(resolvedAnimationMode: string): boolean {
  return resolvedAnimationMode === "normal";
}

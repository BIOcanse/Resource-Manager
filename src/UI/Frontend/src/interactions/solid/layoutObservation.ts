/** ResizeObserver batches all reads before writes after browser layout. */
export interface LayoutSize { readonly width: number; readonly height: number }
export interface LayoutObservation {
  read: (sizes: ReadonlyMap<Element, LayoutSize>) => void;
  write: () => void;
}
const owners = new Map<Element, Set<LayoutObservation>>();
const sizes = new Map<Element, LayoutSize>();
let observer: ResizeObserver | null = null;

export function observeLayout(observation: LayoutObservation) {
  if (!observer) observer = new ResizeObserver((entries) => {
    const changed = new Set<LayoutObservation>();
    for (const entry of entries) {
      const owner = owners.get(entry.target);
      if (owner) {
        const size = entry.borderBoxSize[0];
        sizes.set(entry.target, { width: size.inlineSize, height: size.blockSize });
        for (const observation of owner) changed.add(observation);
      }
    }
    for (const owner of changed) owner.read(sizes);
    for (const owner of changed) owner.write();
  });
  const resize = observer;
  let targets = new Set<Element>();
  return {
    update(next: readonly Element[]) {
      const nextTargets = new Set(next);
      for (const target of targets) if (!nextTargets.has(target)) {
        const targetOwners = owners.get(target)!;
        targetOwners.delete(observation);
        if (!targetOwners.size) {
          resize.unobserve(target);
          owners.delete(target); sizes.delete(target);
        }
      }
      for (const target of nextTargets) if (!targets.has(target)) {
        const targetOwners = owners.get(target) ?? new Set<LayoutObservation>();
        targetOwners.add(observation);
        owners.set(target, targetOwners);
        // Reobserve when adding a reader; observing the same box again would not produce an initial notification.
        if (targetOwners.size > 1) resize.unobserve(target);
        resize.observe(target, { box: "border-box" });
      }
      targets = nextTargets;
    },
    dispose() {
      for (const target of targets) {
        const targetOwners = owners.get(target)!;
        targetOwners.delete(observation);
        if (!targetOwners.size) {
          resize.unobserve(target);
          owners.delete(target); sizes.delete(target);
        }
      }
      targets.clear();
      if (!owners.size) resize.disconnect();
    }
  };
}

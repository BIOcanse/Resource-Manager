/** ResizeObserver 在浏览器布局后通知；同一批组件先全部读，再全部写。不轮询、不在挂载或 render 里量框。 */
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
        // 相同 box 的重复 observe 不会重新通知；新增共享读者时显式重订阅，仍在布局后统一交付。
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

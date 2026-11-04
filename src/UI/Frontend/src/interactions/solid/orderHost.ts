import type { Host } from "../protocol/runtime.ts";

/** Presentation owns this order value and updates it only after capturing the current geometry, ensuring retargeting starts from the visible position. These components have no business ports. */
export function createOrderHost(initial: number): { readonly host: Host<number>; readonly set: (next: number, cause: string) => void } {
  let value = initial;
  const listeners = new Set<(old: number, next: number, cause: string) => void>();
  return {
    host: {
      value: () => value,
      enabled: () => true,
      call: () => { throw new Error("此组件没有端口，不会调用宿主"); },
      subscribe: (listener) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
      }
    },
    set: (next, cause) => {
      const old = value;
      if (old === next) return;
      value = next;
      for (const listener of listeners) listener(old, next, cause);
    }
  };
}

import type { Host } from "../protocol/runtime.ts";

/**
 * 只由呈现层改值的宿主：选中指示条和内容过渡的宿主值是“现在选中的那一项（显示的那份内容）的序号”。
 * 值不来自 Solid 的 effect，而由钩子在量好旧位置之后亲手改（set），所以“先记下这一刻画到哪，再告诉运行时终态变了”的次序是确定的。
 * 没有调用：这两种组件没有端口。
 */
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

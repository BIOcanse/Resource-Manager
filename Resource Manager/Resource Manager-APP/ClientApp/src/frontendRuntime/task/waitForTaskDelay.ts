export type TaskDelay = (
  signal: AbortSignal,
  delayMs: number
) => Promise<void>;

export const waitForTaskDelay: TaskDelay = (signal, delayMs) => {
  if (signal.aborted || !Number.isFinite(delayMs) || delayMs <= 0) {
    return Promise.resolve();
  }
  return new Promise<void>((resolve) => {
    let settled = false;
    const timer = globalThis.setTimeout(finish, Math.floor(delayMs));
    signal.addEventListener("abort", finish, { once: true });

    function finish() {
      if (settled) {
        return;
      }
      settled = true;
      globalThis.clearTimeout(timer);
      signal.removeEventListener("abort", finish);
      resolve();
    }
  });
};

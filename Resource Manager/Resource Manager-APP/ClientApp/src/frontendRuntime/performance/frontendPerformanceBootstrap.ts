import type {
  FrontendPerformanceMonitor
} from "./FrontendPerformanceMonitor.ts";

export const disabledFrontendPerformanceMonitor: FrontendPerformanceMonitor = Object.freeze({
  enabled: false,
  requestObserver: undefined,
  sourceObserver: undefined,
  recordTaskChange: () => undefined,
  markLifecycle: () => undefined,
  markRouteIntent: () => undefined,
  markRouteCommit: () => undefined,
  snapshot: () => null,
  reset: () => undefined,
  dispose: () => undefined
});

export function frontendPerformanceBootstrapRequested(
  target: Record<string, unknown>
): boolean {
  const value = target.__resourceManagerFrontendPerformanceBootstrap;
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return false;
  }
  const bootstrap = value as Record<string, unknown>;
  return bootstrap.schemaVersion === 1
    && bootstrap.enabled === true
    && typeof bootstrap.capacity === "number"
    && Number.isSafeInteger(bootstrap.capacity)
    && bootstrap.capacity >= 256
    && bootstrap.capacity <= 100_000;
}

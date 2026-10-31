export interface NavigationFailureRecord { method?: string; failure?: string; url: string }
export interface NavigationCancellationContext {
  expectedOrigin: string;
  transitionTargetPageId: string | null;
  transitionDeadlineMilliseconds: number;
  observedAtMilliseconds: number;
}
export function isExpectedNavigationCancellation(
  record: NavigationFailureRecord | null | undefined,
  {
    expectedOrigin,
    transitionTargetPageId,
    transitionDeadlineMilliseconds,
    observedAtMilliseconds
  }: NavigationCancellationContext) {
  if (record?.method !== "GET" || record?.failure !== "net::ERR_ABORTED") return false;
  if (typeof transitionTargetPageId !== "string" || transitionTargetPageId.length === 0) {
    return false;
  }
  if (!Number.isFinite(transitionDeadlineMilliseconds)
      || !Number.isFinite(observedAtMilliseconds)
      || observedAtMilliseconds > transitionDeadlineMilliseconds) {
    return false;
  }

  try {
    const url = new URL(record.url);
    return url.origin === expectedOrigin && url.pathname.startsWith("/api/");
  } catch {
    return false;
  }
}

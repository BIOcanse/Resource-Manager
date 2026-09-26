export function isExpectedNavigationCancellation(
  record,
  {
    expectedOrigin,
    transitionTargetPageId,
    transitionDeadlineMilliseconds,
    observedAtMilliseconds
  }) {
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

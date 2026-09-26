export type ObservationStatus =
  | "loading"
  | "ready"
  | "stale"
  | "error"
  | "profile-disabled";

export type ObservationSource =
  | "live"
  | "backend-retained"
  | "cache"
  | "none"
  | "profile";

export interface ObservationState {
  status: ObservationStatus;
  source: ObservationSource;
  capturedAt?: string;
  lastError?: string;
}

export function loadingObservation(): ObservationState {
  return { status: "loading", source: "none" };
}

export function cachedObservation(capturedAt?: string): ObservationState {
  return {
    status: "stale",
    source: "cache",
    capturedAt: normalizeTimestamp(capturedAt)
  };
}

export function readyObservation(capturedAt?: string): ObservationState {
  return {
    status: "ready",
    source: "live",
    capturedAt: normalizeTimestamp(capturedAt)
  };
}

export function backendRetainedObservation(
  capturedAt: string | null | undefined,
  message?: string
): ObservationState {
  return {
    status: "stale",
    source: "backend-retained",
    capturedAt: normalizeTimestamp(capturedAt ?? undefined),
    lastError: message
  };
}

export function refreshingObservation(previous: ObservationState): ObservationState {
  return observationCanRender(previous) ? previous : loadingObservation();
}

export function failedObservation(
  previous: ObservationState,
  message: string): ObservationState
{
  if (observationCanRender(previous)) {
    return {
      ...previous,
      status: "stale",
      lastError: message
    };
  }

  return {
    status: "error",
    source: "none",
    lastError: message
  };
}

export function profileDisabledObservation(reason: string): ObservationState {
  return {
    status: "profile-disabled",
    source: "profile",
    lastError: reason
  };
}

export function observationCanRender(state: ObservationState) {
  return state.status === "ready" || state.status === "stale";
}

function normalizeTimestamp(value?: string) {
  if (!value || !Number.isFinite(Date.parse(value))) {
    return undefined;
  }

  return value;
}

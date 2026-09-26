export const frontendVisibilitySurfaceAttribute = "data-frontend-visibility-surface";
export const frontendVisibilitySurfaceSelector = `[${frontendVisibilitySurfaceAttribute}]`;
export const frontendVisibilityDemandsAttribute = "data-frontend-visibility-demands";

export interface FrontendVisibilitySurfaceAttributes {
  "data-frontend-visibility-surface": string;
  "data-frontend-visibility-demands": string;
}

export interface ParsedFrontendVisibilitySurface {
  surfaceId: string;
  demandIds: readonly string[];
}

export function frontendVisibilitySurface(
  surfaceId: string,
  demandIds: readonly string[]
): FrontendVisibilitySurfaceAttributes {
  const normalizedSurfaceId = normalizeIdentifier(surfaceId, "Frontend visibility surface");
  const normalizedDemandIds = normalizeDemandIds(demandIds);
  if (normalizedDemandIds.length === 0) {
    throw new Error("Frontend visibility surface requires at least one demand ID.");
  }

  return {
    "data-frontend-visibility-surface": normalizedSurfaceId,
    "data-frontend-visibility-demands": normalizedDemandIds.join(" ")
  };
}

export function frontendVisibilityDemandId(prefix: string, sourceId: unknown) {
  const normalizedPrefix = normalizeIdentifier(prefix, "Frontend visibility demand prefix");
  const normalizedSourceId = String(sourceId ?? "").trim() || "unknown";
  return `${normalizedPrefix}.${encodeURIComponent(normalizedSourceId)}`;
}

export function readFrontendVisibilitySurface(
  element: HTMLElement
): ParsedFrontendVisibilitySurface | null {
  const surfaceId = element.getAttribute(frontendVisibilitySurfaceAttribute)?.trim() ?? "";
  if (!surfaceId) {
    return null;
  }

  const demandIds = normalizeDemandIds(
    (element.getAttribute(frontendVisibilityDemandsAttribute) ?? "").split(/\s+/));
  if (demandIds.length === 0) {
    return null;
  }

  return { surfaceId, demandIds };
}

function normalizeIdentifier(value: string, subject: string) {
  const normalized = value.trim();
  if (!normalized) {
    throw new Error(`${subject} requires a non-empty ID.`);
  }
  return normalized;
}

function normalizeDemandIds(demandIds: readonly string[]) {
  return Array.from(new Set(demandIds
    .map((demandId) => demandId.trim())
    .filter(Boolean)))
    .sort();
}

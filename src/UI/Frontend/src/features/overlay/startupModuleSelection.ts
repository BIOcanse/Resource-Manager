import {
  defineResponseDecoder,
  requireArray,
  requireNonEmptyString,
  requireRecord,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { OverlayInjectionModules } from "../../types.ts";

/** The backend registers startup injection only for these two module IDs. */
export const overlayStartupModuleIds = ["performance-overlay-preload", "performance-overlay-vulkan"] as const;

export interface StartupModuleSelection {
  readonly softwareId: string;
  readonly moduleIds: readonly string[];
}

export const startupModuleSelectionDecoder = defineResponseDecoder<StartupModuleSelection>(
  "startup-modules.software-selection.v1",
  (value) => {
    const row = requireRecord(value);
    const moduleIds = requireArray(row.moduleIds, "$.moduleIds")
      .map((id, index) => requireNonEmptyString(id, `$.moduleIds[${index}]`));
    if (new Set(moduleIds).size !== moduleIds.length) {
      throw new ResponseDecodeError("$.moduleIds", "unique module IDs");
    }
    return { softwareId: requireNonEmptyString(row.softwareId, "$.softwareId"), moduleIds };
  });

/** Report whether none, some, or all required modules are registered. */
export function overlayInjectionModules(selection: StartupModuleSelection): OverlayInjectionModules {
  const present = overlayStartupModuleIds.filter((id) => selection.moduleIds.includes(id)).length;
  return present === 0 ? "none" : present === overlayStartupModuleIds.length ? "complete" : "partial";
}

/** Register all required modules only when the overlay is enabled and uses injection. */
export function expectedOverlayInjectionModules(settings: { readonly enabled: boolean; readonly mode: "external" | "injected" }): OverlayInjectionModules {
  return settings.enabled && settings.mode === "injected" ? "complete" : "none";
}

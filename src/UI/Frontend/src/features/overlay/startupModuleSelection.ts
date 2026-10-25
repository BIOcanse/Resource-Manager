import {
  defineResponseDecoder,
  requireArray,
  requireNonEmptyString,
  requireRecord,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { OverlayInjectionModules } from "../../types.ts";

/** 注入叠加层需要的两个启动模块；后端只按这两个 ID 登记启动注入。 */
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

/** 后端当前为这个软件登记了多少叠加层注入模块：都没有、只有一部分、两个都有。 */
export function overlayInjectionModules(selection: StartupModuleSelection): OverlayInjectionModules {
  const present = overlayStartupModuleIds.filter((id) => selection.moduleIds.includes(id)).length;
  return present === 0 ? "none" : present === overlayStartupModuleIds.length ? "complete" : "partial";
}

/** 按界面的选择，后端应当登记的注入模块：只有启用且为注入方式时才登记全部模块。 */
export function expectedOverlayInjectionModules(settings: { readonly enabled: boolean; readonly mode: "external" | "injected" }): OverlayInjectionModules {
  return settings.enabled && settings.mode === "injected" ? "complete" : "none";
}

import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import { defineResponseDecoder } from "../frontendRuntime/request/ResponseDecoder.ts";
import type { SourceRegistry } from "../frontendRuntime/source/SourceRegistry.ts";
import type { SourceHandle } from "../frontendRuntime/source/SourceDescriptor.ts";
import type { AccessKeySnapshot } from "../features/trustedAccess/trustedAccessTypes.ts";
import { reportRuleSettingsDecoder } from "./appSettings/reportRuleSettingsDecoder.ts";
import { memoryCpuTierReadingDecoder } from "./memoryCpuTier/memoryCpuTierDecoder.ts";
import { accessKeySnapshotDecoder } from "../features/trustedAccess/trustedAccessDecoder.ts";
import { classifyTrustedAccessFailure, TrustedAccessRequestError } from "../features/trustedAccess/trustedAccessApi.ts";
import { parseUtcTime } from "./productUpdateStatus.ts";
import { uiText } from "../text.ts";

export interface ProductVersionOption {
  choice: string;
  version: string;
  series?: string | null;
  channel?: string | null;
  publishedAt?: string | null;
  available?: boolean;
  assetName: string;
  downloadUrl: string;
  checksumUrl: string;
  selectable: boolean;
  eligibility: "eligible" | "installedVersionUnknown" | "notNewer";
}

export interface ProductVersionCatalog {
  installedVersion?: string | null;
  status: "loaded" | "partial" | "stale" | "error";
  complete: boolean;
  checkedAt: string;
  options: ProductVersionOption[];
  errorCode?: string | null;
  errorParameters?: Record<string, string> | null;
}

/** Preserve the catalog's existing UTC check at its request boundary. */
export const productVersionCatalogDecoder = defineResponseDecoder<ProductVersionCatalog>(
  "updates.product.versions.v1", (value) => {
    const catalog = value as ProductVersionCatalog;
    if (!parseUtcTime(catalog.checkedAt)) throw new Error("catalog checkedAt is not UTC");
    return catalog;
  });

/** Default-key commands require a fresh list after changing the UI's credentials. */
export async function refreshAccessKeysAfterCommand(
  source: SourceHandle<AccessKeySnapshot>, command: () => Promise<void>
): Promise<void> {
  const lease = source.acquire({ active: false });
  try {
    await command();
    await lease.refresh({ afterCurrent: true });
  } finally {
    lease.release();
  }
}

export function createSettingsReadSources(registry: SourceRegistry, client: RequestClient) {
  return Object.freeze({
    reportRules: registry.define({
      key: "settings.report-rules",
      retainLastGood: false,
      load: (signal) => client.request({ key: "settings.report-rules", url: "/api/settings/report-rules",
        decoder: reportRuleSettingsDecoder, fallbackError: uiText.apiError.readFailed, signal, request: { method: "GET" } })
    }),
    // 内存与 CPU 取舍：后端给出档位、每个选项的生效值和五档的值；保存后由设置分区重读。
    memoryCpuTier: registry.define({
      key: "settings.memory-cpu-tier",
      retainLastGood: false,
      load: (signal) => client.request({ key: "settings.memory-cpu-tier", url: "/api/settings/memory-cpu-tier",
        decoder: memoryCpuTierReadingDecoder, fallbackError: uiText.apiError.readFailed, signal, request: { method: "GET" } })
    }),
    accessKeys: registry.define({
      key: "settings.access-keys",
      retainLastGood: false,
      load: async (signal) => {
        try {
          return await client.request({ key: "settings.access-keys", url: "/api/access-keys",
            decoder: accessKeySnapshotDecoder, fallbackError: uiText.apiError.readFailed, signal, request: { method: "GET" } });
        } catch (error) {
          throw new TrustedAccessRequestError(classifyTrustedAccessFailure(error), error);
        }
      }
    }),
    productVersions: registry.define({
      key: "updates.product.versions",
      retainLastGood: false,
      load: (signal) => client.request({ key: "updates.product.versions", url: "/api/updates/product/versions",
        decoder: productVersionCatalogDecoder, fallbackError: uiText.apiError.readFailed,
        timeoutMs: 35000, signal, request: { method: "GET" } })
    })
  });
}

import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import {
  softwareMetadataLookupDecoder,
  type SoftwareMetadataLookupResult
} from "./softwareMetadataDecoder.ts";
import { uiText } from "../../text.ts";

type SoftwareMetadataRequestClient = Pick<RequestClient, "request">;

export interface SoftwareMetadataQuery {
  readonly softwareIdentityId: string;
  readonly language: string;
}

export async function getSoftwareMetadata(
  requestClient: SoftwareMetadataRequestClient,
  query: SoftwareMetadataQuery,
  signal?: AbortSignal
): Promise<SoftwareMetadataLookupResult> {
  const normalized = normalizeSoftwareMetadataQuery(query);
  const params = new URLSearchParams({ language: normalized.language });
  const result = await requestClient.request({
    key: `software-metadata.${normalized.softwareIdentityId}.${normalized.language}`,
    url: `/api/software/metadata/${encodeURIComponent(normalized.softwareIdentityId)}?${params.toString()}`,
    fallbackError: uiText.misc.softwareMetadataReadFailed,
    decoder: softwareMetadataLookupDecoder,
    signal,
    request: { method: "GET" }
  });
  if (result.metadata
    && result.metadata.softwareIdentityId !== normalized.softwareIdentityId) {
    throw new ResponseDecodeError(
      "$.metadata.softwareIdentityId",
      `'${normalized.softwareIdentityId}'`);
  }
  return result;
}

export function normalizeSoftwareMetadataQuery(
  query: SoftwareMetadataQuery
): SoftwareMetadataQuery {
  const softwareIdentityId = query.softwareIdentityId.trim();
  const language = query.language.trim();
  if (!softwareIdentityId) {
    throw new Error("A software identity ID is required.");
  }
  if (!language) {
    throw new Error("A resolved software metadata language is required.");
  }
  if (language.toLowerCase() === "system") {
    throw new Error("Software metadata requires a concrete resolved language.");
  }
  return Object.freeze({ softwareIdentityId, language });
}

export function softwareMetadataQueryKey(query: SoftwareMetadataQuery): string {
  return JSON.stringify(normalizeSoftwareMetadataQuery(query));
}

export type {
  SoftwareMetadataLookupResult,
  SoftwareMetadataView
} from "./softwareMetadataDecoder.ts";

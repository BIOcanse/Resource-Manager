import type { SourceSnapshot } from
  "../frontendRuntime/source/SourceSnapshot.ts";
import { sourceCanRender } from
  "../frontendRuntime/source/SourceSnapshot.ts";
import type { SoftwareDetailModel } from "../types.ts";
import type { SoftwareMetadataQuery } from "./softwareMetadataApi.ts";
import type {
  SoftwareMetadataLookupResult,
  SoftwareMetadataView
} from "./softwareMetadataDecoder.ts";

export function projectSoftwareMetadataDetail(
  current: SoftwareDetailModel | null,
  query: SoftwareMetadataQuery,
  snapshot: SourceSnapshot<SoftwareMetadataLookupResult>
): SoftwareDetailModel | null {
  if (!current
    || current.type !== "software"
    || current.softwareIdentityId !== query.softwareIdentityId
    || !sourceCanRender(snapshot)
    || !snapshot.data) {
    return current;
  }

  const metadata = snapshot.data.metadata;
  return {
    ...current,
    metadataRows: snapshot.data.found && metadata
      ? createSoftwareMetadataRows(metadata)
      : []
  };
}

export function createSoftwareMetadataRows(
  metadata: SoftwareMetadataView
): SoftwareDetailModel["metadataRows"] {
  return [
    ["软件简介", metadata.summary],
    ["详细说明", metadata.description],
    ["发布者", metadata.publisher],
    ["官方网站", metadata.homepageUrl],
    ["支持网站", metadata.supportUrl],
    ["许可证", metadata.licenseName],
    ["许可证链接", metadata.licenseUrl]
  ];
}

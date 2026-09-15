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
import { uiText } from "../text.ts";

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
    [uiText.softwareMetadata.summary, metadata.summary],
    [uiText.softwareMetadata.description, metadata.description],
    [uiText.softwareMetadata.publisher, metadata.publisher],
    [uiText.softwareMetadata.homepage, metadata.homepageUrl],
    [uiText.softwareMetadata.supportSite, metadata.supportUrl],
    [uiText.softwareMetadata.license, metadata.licenseName],
    [uiText.softwareMetadata.licenseUrl, metadata.licenseUrl]
  ];
}

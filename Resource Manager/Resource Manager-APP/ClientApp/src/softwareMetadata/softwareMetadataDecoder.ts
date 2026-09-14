import {
  defineResponseDecoder,
  requireBoolean,
  requireNonEmptyString,
  requireRecord,
  requireStringArray,
  ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";

export interface SoftwareMetadataView {
  readonly softwareIdentityId: string;
  readonly requestedLanguage: string;
  readonly resolvedLanguage: string;
  readonly summary: string;
  readonly description: string | null;
  readonly publisher: string | null;
  readonly homepageUrl: string | null;
  readonly supportUrl: string | null;
  readonly licenseName: string | null;
  readonly licenseUrl: string | null;
  readonly tags: readonly string[];
  readonly sourceRefs: readonly string[];
}

export interface SoftwareMetadataLookupResult {
  readonly catalogVersion: string;
  readonly found: boolean;
  readonly metadata: SoftwareMetadataView | null;
}

export const softwareMetadataLookupDecoder =
  defineResponseDecoder<SoftwareMetadataLookupResult>(
    "software-metadata.lookup.v1",
    (value) => {
      const record = requireRecord(value);
      const catalogVersion = requireNonEmptyString(
        record.catalogVersion,
        "$.catalogVersion");
      const found = requireBoolean(record.found, "$.found");
      const metadata = record.metadata === null
        ? null
        : decodeMetadata(record.metadata);
      if (found !== (metadata !== null)) {
        throw new ResponseDecodeError(
          "$.metadata",
          found ? "metadata object when found is true" : "null when found is false");
      }
      return Object.freeze({ catalogVersion, found, metadata });
    });

function decodeMetadata(value: unknown): SoftwareMetadataView {
  const record = requireRecord(value, "$.metadata");
  return Object.freeze({
    softwareIdentityId: requireNonEmptyString(
      record.softwareIdentityId,
      "$.metadata.softwareIdentityId"),
    requestedLanguage: requireNonEmptyString(
      record.requestedLanguage,
      "$.metadata.requestedLanguage"),
    resolvedLanguage: requireNonEmptyString(
      record.resolvedLanguage,
      "$.metadata.resolvedLanguage"),
    summary: requireNonEmptyString(record.summary, "$.metadata.summary"),
    description: optionalString(record.description, "$.metadata.description"),
    publisher: optionalString(record.publisher, "$.metadata.publisher"),
    homepageUrl: optionalString(record.homepageUrl, "$.metadata.homepageUrl"),
    supportUrl: optionalString(record.supportUrl, "$.metadata.supportUrl"),
    licenseName: optionalString(record.licenseName, "$.metadata.licenseName"),
    licenseUrl: optionalString(record.licenseUrl, "$.metadata.licenseUrl"),
    tags: Object.freeze(requireStringArray(record.tags, "$.metadata.tags")),
    sourceRefs: Object.freeze(requireStringArray(
      record.sourceRefs,
      "$.metadata.sourceRefs"))
  });
}

function optionalString(value: unknown, path: string): string | null {
  return value === null ? null : requireNonEmptyString(value, path);
}

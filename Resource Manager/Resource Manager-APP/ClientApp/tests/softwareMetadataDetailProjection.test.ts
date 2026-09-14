import assert from "node:assert/strict";
import test from "node:test";
import type { SourceSnapshot } from
  "../src/frontendRuntime/source/SourceSnapshot.ts";
import type { SoftwareMetadataQuery } from
  "../src/softwareMetadata/softwareMetadataApi.ts";
import {
  projectSoftwareMetadataDetail
} from "../src/softwareMetadata/softwareMetadataDetailProjection.ts";
import type { SoftwareMetadataLookupResult } from
  "../src/softwareMetadata/softwareMetadataDecoder.ts";
import type { SoftwareDetailModel } from "../src/types.ts";

test("loading and failed refreshes preserve the last renderable metadata", () => {
  const model = detailModel("app-test", [["软件简介", "last good"]]);
  const query = metadataQuery("app-test", "zh-CN");

  assert.equal(
    projectSoftwareMetadataDetail(model, query, snapshot("loading", null)),
    model);
  assert.equal(
    projectSoftwareMetadataDetail(model, query, snapshot("error", null)),
    model);
});

test("a ready language switch replaces metadata without touching another identity", () => {
  const chineseModel = projectSoftwareMetadataDetail(
    detailModel("app-test"),
    metadataQuery("app-test", "zh-CN"),
    readySnapshot("zh-CN", "中文说明"));
  assert.equal(chineseModel?.metadataRows[0]?.[1], "中文说明");

  const englishModel = projectSoftwareMetadataDetail(
    chineseModel,
    metadataQuery("app-test", "en-US"),
    readySnapshot("en-US", "English summary"));
  assert.equal(englishModel?.metadataRows[0]?.[1], "English summary");

  const other = detailModel("app-other", [["软件简介", "other"]]);
  assert.equal(
    projectSoftwareMetadataDetail(
      other,
      metadataQuery("app-test", "en-US"),
      readySnapshot("en-US", "wrong identity")),
    other);
});

test("an authoritative not-found result clears only the matching detail", () => {
  const model = detailModel("app-test", [["软件简介", "stale"]]);
  const projected = projectSoftwareMetadataDetail(
    model,
    metadataQuery("app-test", "en-US"),
    snapshot("ready", {
      catalogVersion: "1.0.0",
      found: false,
      metadata: null
    }));

  assert.deepEqual(projected?.metadataRows, []);
});

function detailModel(
  softwareIdentityId: string,
  metadataRows: SoftwareDetailModel["metadataRows"] = []
): SoftwareDetailModel {
  return {
    type: "software",
    id: softwareIdentityId,
    name: softwareIdentityId,
    kind: "Other",
    displayKind: "软件",
    state: "Ready",
    message: "",
    dataSearchName: softwareIdentityId,
    rootPaths: [],
    suggestedRootPaths: [],
    executablePaths: [],
    requiresRootPathConfirmation: false,
    identityConfirmed: true,
    issues: [],
    rootMigrationPaths: [],
    rootMigrationDisabledReason: "",
    baseRows: [],
    softwareIdentityId,
    metadataRows,
    pathRows: [],
    operationRows: [],
    capabilities: [],
    providers: []
  };
}

function metadataQuery(
  softwareIdentityId: string,
  language: string
): SoftwareMetadataQuery {
  return { softwareIdentityId, language };
}

function readySnapshot(
  language: string,
  summary: string
): SourceSnapshot<SoftwareMetadataLookupResult> {
  return snapshot("ready", {
    catalogVersion: "1.0.0",
    found: true,
    metadata: {
      softwareIdentityId: "app-test",
      requestedLanguage: language,
      resolvedLanguage: language,
      summary,
      description: null,
      publisher: null,
      homepageUrl: null,
      supportUrl: null,
      licenseName: null,
      licenseUrl: null,
      tags: [],
      sourceRefs: ["resource-manager-curation"]
    }
  });
}

function snapshot(
  status: SourceSnapshot<SoftwareMetadataLookupResult>["status"],
  data: SoftwareMetadataLookupResult | null
): SourceSnapshot<SoftwareMetadataLookupResult> {
  return {
    key: "software.metadata.detail",
    status,
    data,
    error: status === "error" ? new Error("refresh failed") : null,
    backendEpoch: "epoch-a",
    revision: data ? 1 : 0,
    acceptedAttempt: data ? 1 : null,
    domainRevision: null,
    capturedAt: null
  };
}

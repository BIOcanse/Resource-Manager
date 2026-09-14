import assert from "node:assert/strict";
import { SoftwareMetadataLeaseBinding } from
  "../src/softwareMetadata/SoftwareMetadataLeaseBinding.ts";
import type { SoftwareMetadataQuery } from
  "../src/softwareMetadata/softwareMetadataApi.ts";
import type { SoftwareMetadataLookupResult } from
  "../src/softwareMetadata/softwareMetadataDecoder.ts";
import type {
  SourceDemand,
  SourceFamily,
  SourceLease
} from "../src/frontendRuntime/source/SourceDescriptor.ts";
import type { SourceSnapshot } from
  "../src/frontendRuntime/source/SourceSnapshot.ts";

const owned: ReturnType<typeof fakeLease>[] = [];
let acquireFailureLanguage: string | null = null;
let subscribeFailureLanguage: string | null = null;
const source: SourceFamily<SoftwareMetadataQuery, SoftwareMetadataLookupResult> = {
  key: "software.metadata.detail",
  acquire(query, demand) {
    if (query.language === acquireFailureLanguage) {
      throw new Error("acquire failed");
    }
    const lease = fakeLease(
      query,
      demand,
      query.language === subscribeFailureLanguage
        ? new Error("subscribe failed")
        : null);
    owned.push(lease);
    return lease.lease;
  }
};
const publications: Array<{
  query: SoftwareMetadataQuery;
  snapshot: SourceSnapshot<SoftwareMetadataLookupResult>;
}> = [];
let publishFailureLanguage: string | null = null;
const binding = new SoftwareMetadataLeaseBinding(
  source,
  (query, snapshot) => {
    if (query.language === publishFailureLanguage) {
      throw new Error("publish failed");
    }
    publications.push({ query, snapshot });
  });

binding.switch({ softwareIdentityId: "app-test", language: "zh-CN" });
assert.equal(owned.length, 1);
assert.deepEqual(owned[0]?.initialDemand, {
  active: true,
  refreshIntervalMs: null
});
assert.equal(publications.length, 1);

binding.switch({ softwareIdentityId: "app-test", language: "zh-CN" });
assert.equal(owned.length, 1);
assert.equal(publications.length, 2);

binding.switch({ softwareIdentityId: "app-test", language: "en-US" });
assert.equal(owned.length, 2);
assert.equal(owned[0]?.releaseCount(), 1);
assert.equal(owned[1]?.releaseCount(), 0);

const publicationCount = publications.length;
owned[0]?.emit(readySnapshot("zh-CN"));
assert.equal(publications.length, publicationCount);
owned[1]?.emit(readySnapshot("en-US"));
assert.equal(publications.at(-1)?.query.language, "en-US");
assert.equal(publications.at(-1)?.snapshot.data?.metadata?.summary, "en-US summary");

acquireFailureLanguage = "de-DE";
assert.throws(
  () => binding.switch({ softwareIdentityId: "app-test", language: "de-DE" }),
  /acquire failed/);
acquireFailureLanguage = null;
assert.equal(owned[1]?.releaseCount(), 0);

subscribeFailureLanguage = "fr-FR";
assert.throws(
  () => binding.switch({ softwareIdentityId: "app-test", language: "fr-FR" }),
  /subscribe failed/);
subscribeFailureLanguage = null;
assert.equal(owned[2]?.releaseCount(), 1);
assert.equal(owned[1]?.releaseCount(), 0);

publishFailureLanguage = "ja-JP";
assert.throws(
  () => binding.switch({ softwareIdentityId: "app-test", language: "ja-JP" }),
  /publish failed/);
publishFailureLanguage = null;
assert.equal(owned[3]?.releaseCount(), 1);
assert.equal(owned[1]?.releaseCount(), 0);

owned[1]?.emit(readySnapshot("en-US"));
assert.equal(publications.at(-1)?.query.language, "en-US");

binding.release();
assert.equal(owned[1]?.releaseCount(), 1);

function fakeLease(
  query: SoftwareMetadataQuery,
  initialDemand?: SourceDemand,
  subscribeError: Error | null = null
) {
  let current = loadingSnapshot(query.language);
  let releases = 0;
  const listeners = new Set<
    (snapshot: SourceSnapshot<SoftwareMetadataLookupResult>) => void
  >();
  const lease: SourceLease<SoftwareMetadataLookupResult> = {
    key: `software.metadata.detail:${query.language}`,
    get snapshot() {
      return current;
    },
    subscribe(listener) {
      if (subscribeError) {
        throw subscribeError;
      }
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    setDemand() {},
    refresh() {
      return Promise.resolve(current);
    },
    acceptAuthoritative() {
      throw new Error("Not used by this fixture.");
    },
    release() {
      releases += 1;
      listeners.clear();
    }
  };
  return {
    lease,
    initialDemand,
    releaseCount: () => releases,
    emit(snapshot: SourceSnapshot<SoftwareMetadataLookupResult>) {
      current = snapshot;
      for (const listener of listeners) {
        listener(snapshot);
      }
    }
  };
}

function loadingSnapshot(language: string): SourceSnapshot<SoftwareMetadataLookupResult> {
  return snapshot(language, "loading", null, 0);
}

function readySnapshot(language: string): SourceSnapshot<SoftwareMetadataLookupResult> {
  return snapshot(language, "ready", {
    catalogVersion: "1.0.0",
    found: true,
    metadata: {
      softwareIdentityId: "app-test",
      requestedLanguage: language,
      resolvedLanguage: language,
      summary: `${language} summary`,
      description: null,
      publisher: null,
      homepageUrl: null,
      supportUrl: null,
      licenseName: null,
      licenseUrl: null,
      tags: [],
      sourceRefs: ["curated"]
    }
  }, 1);
}

function snapshot(
  language: string,
  status: SourceSnapshot<SoftwareMetadataLookupResult>["status"],
  data: SoftwareMetadataLookupResult | null,
  revision: number
): SourceSnapshot<SoftwareMetadataLookupResult> {
  return {
    key: `software.metadata.detail:${language}`,
    status,
    data,
    error: null,
    backendEpoch: "epoch-a",
    revision,
    acceptedAttempt: data ? revision : null,
    domainRevision: null,
    capturedAt: null
  };
}

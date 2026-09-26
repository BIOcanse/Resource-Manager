import assert from "node:assert/strict";
import {
  getSoftwareMetadata,
  normalizeSoftwareMetadataQuery
} from "../src/features/softwareMetadata/softwareMetadataApi.ts";
import { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../src/frontendRuntime/request/RequestProblem.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";

const originalFetch = globalThis.fetch;
const owner = new BackendSessionOwner({
  transport: null,
  allowStandalone: true,
  standaloneEpochFactory: () => "standalone:software-metadata-test"
});
const client = new RequestClient(owner);

try {
  {
    let observedUrl = "";
    let observedInit: RequestInit | undefined;
    globalThis.fetch = (async (input, init) => {
      observedUrl = String(input);
      observedInit = init;
      return new Response(JSON.stringify({
        catalogVersion: "1.0.0",
        found: true,
        metadata: {
          softwareIdentityId: "app/test value",
          requestedLanguage: "zh-CN",
          resolvedLanguage: "en-US",
          summary: "English fallback.",
          description: null,
          publisher: null,
          homepageUrl: null,
          supportUrl: null,
          licenseName: null,
          licenseUrl: null,
          tags: [],
          sourceRefs: ["curated"]
        }
      }), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      });
    }) as typeof fetch;

    const value = await getSoftwareMetadata(client, {
      softwareIdentityId: "app/test value",
      language: "zh-CN"
    });
    assert.equal(
      observedUrl,
      "/api/software/metadata/app%2Ftest%20value?language=zh-CN");
    assert.equal(observedInit?.method, "GET");
    assert.equal(observedInit?.cache, "no-store");
    assert.equal(value.metadata?.resolvedLanguage, "en-US");
  }

  {
    assert.throws(
      () => normalizeSoftwareMetadataQuery({
        softwareIdentityId: "app-test",
        language: "system"
      }),
      /concrete resolved language/);
  }

  {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      catalogVersion: "1.0.0",
      found: true,
      metadata: null
    }), { status: 200 })) as typeof fetch;

    await assert.rejects(
      getSoftwareMetadata(client, {
        softwareIdentityId: "app-test",
        language: "en-US"
      }),
      (error: unknown) => error instanceof RequestProblem
        && error.kind === "invalid-response"
        && error.attempt.decoderId === "software-metadata.lookup.v1");
  }

  {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      catalogVersion: "1.0.0",
      found: true,
      metadata: {
        softwareIdentityId: "app-other",
        requestedLanguage: "en-US",
        resolvedLanguage: "en-US",
        summary: "Other application.",
        description: null,
        publisher: null,
        homepageUrl: null,
        supportUrl: null,
        licenseName: null,
        licenseUrl: null,
        tags: [],
        sourceRefs: ["curated"]
      }
    }), { status: 200 })) as typeof fetch;

    await assert.rejects(
      getSoftwareMetadata(client, {
        softwareIdentityId: "app-test",
        language: "en-US"
      }));
  }
} finally {
  owner.dispose();
  globalThis.fetch = originalFetch;
}

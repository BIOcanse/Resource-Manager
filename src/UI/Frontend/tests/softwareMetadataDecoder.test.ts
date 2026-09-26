import assert from "node:assert/strict";
import {
  softwareMetadataLookupDecoder
} from "../src/softwareMetadata/softwareMetadataDecoder.ts";
import {
  ResponseDecodeError
} from "../src/frontendRuntime/request/ResponseDecoder.ts";

const validMetadata = {
  softwareIdentityId: "app-google-chrome",
  requestedLanguage: "zh-CN",
  resolvedLanguage: "zh-CN",
  summary: "Google 推出的网页浏览器。",
  description: null,
  publisher: "Google LLC",
  homepageUrl: "https://www.google.com/chrome/",
  supportUrl: null,
  licenseName: null,
  licenseUrl: null,
  tags: ["browser", "web"],
  sourceRefs: ["winget-community", "resource-manager-curation"]
};

{
  const decoded = softwareMetadataLookupDecoder.decode({
    catalogVersion: "1.0.0",
    found: true,
    metadata: validMetadata
  });
  assert.equal(decoded.found, true);
  assert.equal(decoded.metadata?.resolvedLanguage, "zh-CN");
  assert.equal(decoded.metadata?.summary, "Google 推出的网页浏览器。");
  assert.equal(Object.isFrozen(decoded), true);
  assert.equal(Object.isFrozen(decoded.metadata), true);
  assert.equal(Object.isFrozen(decoded.metadata?.tags), true);
}

{
  const decoded = softwareMetadataLookupDecoder.decode({
    catalogVersion: "1.0.0",
    found: false,
    metadata: null
  });
  assert.deepEqual(decoded, {
    catalogVersion: "1.0.0",
    found: false,
    metadata: null
  });
}

for (const value of [
  { catalogVersion: "1.0.0", found: true, metadata: null },
  { catalogVersion: "1.0.0", found: false, metadata: validMetadata },
  {
    catalogVersion: "1.0.0",
    found: true,
    metadata: { ...validMetadata, tags: "browser" }
  }
]) {
  assert.throws(
    () => softwareMetadataLookupDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError);
}

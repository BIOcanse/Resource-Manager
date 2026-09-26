import assert from "node:assert/strict";
import {
  createFrontendBuildManifest,
  frontendBuildManifestContract
} from "../build/frontendBuildManifest.ts";

const first = createFrontendBuildManifest(
  ["assets/index-b.js", "/assets/index-a.js"],
  ["assets/index.css"]
);
const reordered = createFrontendBuildManifest(
  ["/assets/index-a.js", "assets/index-b.js"],
  ["/assets/index.css"]
);

assert.equal(first.contract, frontendBuildManifestContract);
assert.deepEqual(first.entryScripts, ["/assets/index-a.js", "/assets/index-b.js"]);
assert.deepEqual(first.stylesheets, ["/assets/index.css"]);
assert.equal(first.buildIdentity, reordered.buildIdentity);
assert.equal(
  first.buildIdentity,
  "sha256:47164e74f9b24b99f0797e657046a23f2457654e3afda611bc141ad7c7928156"
);
assert.match(first.buildIdentity, /^sha256:[0-9a-f]{64}$/);
assert.notEqual(
  first.buildIdentity,
  createFrontendBuildManifest(["assets/index-c.js"], ["assets/index.css"]).buildIdentity
);
assert.throws(() => createFrontendBuildManifest([], []), /no entry script/i);
assert.throws(
  () => createFrontendBuildManifest(["../outside.js"], []),
  /invalid frontend asset path/i
);

console.log("frontend build manifest tests passed");

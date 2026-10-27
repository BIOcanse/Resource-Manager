import assert from "node:assert/strict";
import { test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { managementBrowserRuntimesDecoder } from "../../../src/data/managementInventory.ts";

test("Default browser runtime fixture satisfies the production inventory decoder", async () => {
  const server = await startMockServer();
  try {
    const { value } = await server.json("/api/browser-runtimes");
    const decoded = managementBrowserRuntimesDecoder.decode(value);
    assert.equal(decoded.sharedRuntime?.kind, "web_view2_runtime");
    assert.equal(decoded.sharedRuntime?.source, "managed");
    assert.equal(decoded.candidates.length, 1);
  } finally { await server.close(); }
});

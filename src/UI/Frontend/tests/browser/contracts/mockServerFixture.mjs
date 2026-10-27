import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:net";
import { fileURLToPath } from "node:url";

// Exercise the real mock over HTTP, with a private port and no build/static output.
export async function startMockServer({ env = {} } = {}) {
  const reservation = createServer();
  reservation.listen(0, "127.0.0.1");
  await once(reservation, "listening");
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, ["--experimental-transform-types", fileURLToPath(new URL("../mockFrontendServer.mjs", import.meta.url))], {
    env: { ...process.env, ...env, RM_FRONTEND_HARNESS_PORT: String(port) },
    stdio: ["ignore", "pipe", "pipe"], windowsHide: true
  });
  let output = "";
  child.stdout.on("data", chunk => { output += chunk; });
  child.stderr.on("data", chunk => { output += chunk; });
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`Mock startup timed out: ${output}`)), 10_000);
      const check = () => {
        if (output.includes("frontend harness listening")) { clearTimeout(timer); resolve(); }
      };
      child.stdout.on("data", check);
      child.once("error", error => { clearTimeout(timer); reject(error); });
      child.once("exit", code => { clearTimeout(timer); reject(new Error(`Mock exited ${code}: ${output}`)); });
      check();
    });
  } catch (error) { child.kill(); throw error; }
  return {
    baseUrl,
    async close() {
      if (child.exitCode !== null) return;
      const exited = once(child, "exit");
      child.kill();
      await exited;
    },
    async scenario(value) {
      const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) });
      assert.equal(response.status, 200, await response.text());
    },
    async json(path, options) {
      const response = await fetch(`${baseUrl}${path}`, options);
      return { status: response.status, value: await response.json() };
    },
    async frames(subscriptions, predicate) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(new Error("Subscription frames timed out")), 5_000);
      let reader;
      try {
        const response = await fetch(`${baseUrl}/api/subscriptions/stream`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ version: 1, subscriptions }), signal: controller.signal
        });
        assert.equal(response.status, 200, "One invalid selector must not reject the HTTP channel");
        reader = response.body.getReader();
        const decoder = new TextDecoder();
        const frames = [];
        let pending = "";
        while (frames.length < 100) {
          const { done, value } = await reader.read();
          if (done) break;
          pending += decoder.decode(value, { stream: true });
          const lines = pending.split("\n");
          pending = lines.pop();
          for (const line of lines) if (line.trim()) frames.push(JSON.parse(line));
          if (predicate(frames)) return frames;
        }
        assert.fail("Subscription ended without the expected frames");
      } finally {
        clearTimeout(timer);
        controller.abort();
        await reader?.cancel().catch(() => {});
      }
    }
  };
}

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { createServer, type AddressInfo } from "node:net";
import { fileURLToPath } from "node:url";
import type { AppSettingsResult, BackendAppSettings } from "../../../src/types.ts";
import type { HostMemoryProtectionSettings } from "../../../src/data/memoryProtection/memoryProtectionTypes.ts";

// The mock publishes these settings sections and result fields on every settings response.
export type MockAppSettingsResult = AppSettingsResult & {
  settings: BackendAppSettings & {
    debug: NonNullable<BackendAppSettings["debug"]>;
    memoryProtection: HostMemoryProtectionSettings;
    memoryCpuTier: NonNullable<BackendAppSettings["memoryCpuTier"]>;
  };
  revision: string;
  runtimeCapabilityConstrainedPaths: readonly string[];
};

export interface MockSubscription {
  id: string;
  path: string;
  fields?: { deviceIdentity: string; fieldName: string }[];
}
export interface MockFrame<T> { subscriptionId: string; value: T }
export interface MockJsonResult<T> { status: number; value: T }

// Exercise the real mock over HTTP, with a private port and no build/static output.
export async function startMockServer({ env = {} }: { env?: NodeJS.ProcessEnv } = {}) {
  const reservation = createServer();
  reservation.listen(0, "127.0.0.1");
  await once(reservation, "listening");
  const port = (reservation.address() as AddressInfo).port;
  await new Promise<Error | undefined>(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, ["--experimental-transform-types", fileURLToPath(new URL("../mockFrontendServer.ts", import.meta.url))], {
    env: { ...process.env, ...env, RM_FRONTEND_HARNESS_PORT: String(port) },
    stdio: ["ignore", "pipe", "pipe"], windowsHide: true
  });
  let output = "";
  child.stdout.on("data", chunk => { output += chunk; });
  child.stderr.on("data", chunk => { output += chunk; });
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await new Promise<void>((resolve, reject) => {
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
    async scenario(value: unknown) {
      const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) });
      assert.equal(response.status, 200, await response.text());
    },
    async json<T = unknown>(path: string, options?: RequestInit): Promise<MockJsonResult<T>> {
      const response = await fetch(`${baseUrl}${path}`, options);
      // HTTP is the untrusted boundary; each contract test supplies its expected wire shape.
      return { status: response.status, value: await response.json() as T };
    },
    async frames<T = unknown>(subscriptions: MockSubscription[], predicate: (frames: MockFrame<T>[]) => boolean): Promise<MockFrame<T>[]> {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(new Error("Subscription frames timed out")), 5_000);
      let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
      try {
        const response = await fetch(`${baseUrl}/api/subscriptions/stream`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ version: 1, subscriptions }), signal: controller.signal
        });
        assert.equal(response.status, 200, "One invalid selector must not reject the HTTP channel");
        reader = response.body!.getReader();
        const decoder = new TextDecoder();
        const frames: MockFrame<T>[] = [];
        let pending = "";
        while (frames.length < 100) {
          const { done, value } = await reader.read();
          if (done) break;
          pending += decoder.decode(value, { stream: true });
          const lines = pending.split("\n");
          pending = lines.pop()!;
          for (const line of lines) if (line.trim()) frames.push(JSON.parse(line) as MockFrame<T>);
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

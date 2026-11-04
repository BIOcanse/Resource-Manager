import type { SourceRegistry } from "../../frontendRuntime/source/SourceRegistry.ts";
import { requestLoginStartup } from "../../features/settings/shellLoginStartup.ts";

export function createLoginStartupSource(registry: SourceRegistry, read = () => requestLoginStartup()) {
  return registry.define({ key: "shell.login-startup", load: read, retainLastGood: false });
}

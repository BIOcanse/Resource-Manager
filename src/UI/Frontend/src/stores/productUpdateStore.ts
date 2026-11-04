import { usePushSource } from "../frontendRuntime/push/usePushSource.ts";
import { createComputed, createSignal } from "solid-js";
import type { Accessor } from "solid-js";
import { postJson } from "../api.ts";
import type { CurrentValueSource } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import {
  productUpdateCopy,
  updateFailure,
  type ProductUpdateState
} from "../features/settings/productUpdatePresentation.ts";
import { postShellMessage } from "../utils.ts";

/* Shared update state for settings and notifications. Command replies update it; subsequent push frames replace it. */
export interface ProductUpdateStore {
  /** Latest received update status; null before the first response. */
  readonly status: Accessor<ProductUpdateState | null>;
  /** Push or decode failure; retain the previous status and display it as stale. */
  readonly statusFailed: Accessor<boolean>;
  readonly applyPending: Accessor<boolean>;
  accept(state: ProductUpdateState): void;
  /** Confirm and apply the update; ask the native shell to exit when required. */
  apply(confirm: (targetVersion: string) => boolean, language: string): Promise<void>;
}

export function createProductUpdateStore(options: {
  readonly source: CurrentValueSource<ProductUpdateState>;
  /** Subscribe while visible; resubscribing receives the current state first. */
  readonly active: Accessor<boolean>;
}): ProductUpdateStore {
  const [status, setStatus] = createSignal<ProductUpdateState | null>(null);
  const [statusFailed, setStatusFailed] = createSignal(false);
  const [applyPending, setApplyPending] = createSignal(false);
  const source = usePushSource(options.source, () => ({ active: options.active() }));
  createComputed(() => {
    const snapshot = source.snapshot();
    if (snapshot.data && (snapshot.status === "ready" || snapshot.status === "refreshing")) {
      setStatus(snapshot.data);
      setStatusFailed(false);
    } else if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "stale") {
      setStatusFailed(true);
    }
  });

  async function apply(confirm: (targetVersion: string) => boolean, language: string) {
    const current = status();
    if (applyPending() || statusFailed() || current?.stage !== "pendingApply") return;
    if (!confirm(current.targetVersion ?? "")) return;
    setApplyPending(true);
    try {
      const state = await postJson<ProductUpdateState>("/api/updates/product/apply", {},
        productUpdateCopy(language).error("update.applyFailed"));
      setStatus(state);
      setStatusFailed(false);
      if (state.stage === "waitingForExit") postShellMessage("update.applyExit");
    } catch (error) {
      setStatus(updateFailure(error, "update.applyFailed"));
    } finally { setApplyPending(false); }
  }

  return {
    status, statusFailed, applyPending, apply,
    accept: (state) => { setStatus(state); setStatusFailed(false); }
  };
}

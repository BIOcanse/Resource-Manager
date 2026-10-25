import { createEffect, createSignal, onCleanup } from "solid-js";
import type { Accessor } from "solid-js";
import { postJson } from "../api.ts";
import type { CurrentValueSource } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import {
  productUpdateCopy,
  updateFailure,
  type ProductUpdateState
} from "../features/settings/productUpdatePresentation.ts";
import { postShellMessage } from "../utils.ts";

/*
 * 产品更新状态的前端所有者：订阅后端推送的更新状态（订阅时先发当前状态，之后每变一次发一次），
 * 设置页的更新区和界面内新版本提示读同一份。命令（准备、立即应用）的返回值也写进这里，
 * 之后的推送帧照常覆盖。
 */
export interface ProductUpdateStore {
  /** 最近一次收到的更新状态；还没收到为 null。 */
  readonly status: Accessor<ProductUpdateState | null>;
  /** 推送失败或帧无效；status 保留上一次的值，界面标为过期。 */
  readonly statusFailed: Accessor<boolean>;
  readonly applyPending: Accessor<boolean>;
  accept(state: ProductUpdateState): void;
  /** 现有的“立即应用”：确认后请求应用，等待退出时通知原生壳退出界面。 */
  apply(confirm: (targetVersion: string) => boolean, language: string): Promise<void>;
}

export function createProductUpdateStore(options: {
  readonly source: CurrentValueSource<ProductUpdateState>;
  /** 界面可见时订阅；不可见时释放，重新可见时推送先发当前状态。 */
  readonly active: Accessor<boolean>;
}): ProductUpdateStore {
  const [status, setStatus] = createSignal<ProductUpdateState | null>(null);
  const [statusFailed, setStatusFailed] = createSignal(false);
  const [applyPending, setApplyPending] = createSignal(false);
  createEffect(() => {
    if (!options.active()) return;
    const unsubscribe = options.source.subscribeSnapshot((snapshot) => {
      if (snapshot.data && (snapshot.status === "ready" || snapshot.status === "refreshing")) {
        setStatus(snapshot.data);
        setStatusFailed(false);
      } else if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "stale") {
        setStatusFailed(true);
      }
    });
    onCleanup(unsubscribe);
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

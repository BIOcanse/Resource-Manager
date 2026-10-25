import { createEffect, createSignal, on, onCleanup, Show } from "solid-js";
import { getJson, putJson } from "../api";
import type { ProtectedOptimizationTarget } from "../types";
import { uiText } from "../text";
import { SwitchControl } from "../ui/primitives/Switch.tsx";

export function SoftwareProtectionControl(props: { softwareId: string }) {
  const [enabled, setEnabled] = createSignal(false);
  const [state, setState] = createSignal<"loading" | "ready" | "saving" | "error">("loading");
  let request = 0;
  onCleanup(() => { request++; });
  createEffect(on(() => props.softwareId, (softwareId) => {
    const current = ++request;
    setState("loading");
    void getJson<ProtectedOptimizationTarget[]>("/api/optimization/protection")
      .then((targets) => {
        if (current !== request) return;
        setEnabled(targets.some((target) => target.protectionLevel === 2
          && (target.softwareId?.toLowerCase() === softwareId.toLowerCase()
            || target.targetKey.toLowerCase() === `software:${softwareId}`.toLowerCase())));
        setState("ready");
      }).catch(() => { if (current === request) setState("error"); });
  }));

  async function toggle() {
    if (state() !== "ready") return;
    const softwareId = props.softwareId;
    const current = request;
    const previous = enabled();
    const next = !previous;
    setEnabled(next);
    setState("saving");
    try {
      await putJson(`/api/optimization/protection/software/${encodeURIComponent(softwareId)}`, { enabled: next }, uiText.softwareDetail.protection.failed);
      if (current === request) { setEnabled(next); setState("ready"); }
    } catch { if (current === request) { setEnabled(previous); setState("error"); } }
  }

  // 放在策略页“调度参与”组第一行，与三个“不参与”开关并列但互不联动；保护立即保存，不进策略草稿。
  // 开关沿用设置页的 .settings-switch；整行是 label，点文字也能切换。
  return <div class="software-protection-section" role="group" aria-label={uiText.softwareDetail.protection.title}>
    <label class="policy-toggle">
      <span>{uiText.softwareDetail.protection.enabled}</span>
      <SwitchControl checked={enabled()} disabled={state() !== "ready"} as="span" onChange={() => void toggle()} />
    </label>
    <p class="software-detail-hint">{uiText.softwareDetail.protection.hint}</p>
    <Show when={state() === "error"}><p class="software-protection-message error" role="alert">{uiText.softwareDetail.protection.failed}</p></Show>
    <Show when={state() === "loading" || state() === "saving"}><p class="software-protection-message" role="status">{uiText.softwareDetail.processing}</p></Show>
  </div>;
}

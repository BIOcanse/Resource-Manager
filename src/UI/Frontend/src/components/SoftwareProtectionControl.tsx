import { createEffect, createSignal, on, onCleanup, Show } from "solid-js";
import { getJson, putJson } from "../api";
import type { ProtectedOptimizationTarget } from "../types";
import { protectedTargetListDecoder } from "../data/optimization/optimizationReportsDecoder.ts";
import { isKnownProtectionSource, normalizedLevel, resolveSoftwareProtection } from "../data/optimization/softwareProtection.ts";
import { uiText } from "../text";
import { SwitchControl } from "../ui/primitives/Switch.tsx";

export function SoftwareProtectionControl(props: { softwareId: string; softwareKind: string }) {
  // 决定这个软件保护状态的条目（软件编号的条目，没有再看软件类型的条目）；null 是没有条目适用，即不保护。
  const [deciding, setDeciding] = createSignal<ProtectedOptimizationTarget | null>(null);
  const [enabled, setEnabled] = createSignal(false);
  const [state, setState] = createSignal<"loading" | "ready" | "saving" | "error">("loading");
  let request = 0;
  onCleanup(() => { request++; });

  async function read(softwareId: string, softwareKind: string, current: number) {
    const targets = protectedTargetListDecoder.decode(await getJson<unknown>("/api/optimization/protection"));
    if (current !== request) return;
    const entry = resolveSoftwareProtection(targets, softwareId, softwareKind);
    setDeciding(entry);
    setEnabled(entry !== null && normalizedLevel(entry.protectionLevel) > 0);
    setState("ready");
  }

  createEffect(on(() => [props.softwareId, props.softwareKind] as const, ([softwareId, softwareKind]) => {
    const current = ++request;
    setState("loading");
    read(softwareId, softwareKind, current).catch(() => { if (current === request) setState("error"); });
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
    } catch {
      if (current === request) { setEnabled(previous); setState("error"); }
      return;
    }
    // 写入后重读列表：关掉开关可能让默认条目重新决定（例如软件类型的默认），开关和来源都按重读的结果显示。
    try { await read(softwareId, props.softwareKind, current); }
    catch { if (current === request) setState("error"); }
  }

  // 放在策略页“调度参与”组第一行，与三个“不参与”开关并列但互不联动；保护立即保存，不进策略草稿。
  // 开关沿用设置页的 .settings-switch；整行是 label，点文字也能切换。来源一行在说明下面，样式同说明。
  return <div class="software-protection-section" role="group" aria-label={uiText.softwareDetail.protection.title}>
    <label class="policy-toggle">
      <span>{uiText.softwareDetail.protection.enabled}</span>
      <SwitchControl checked={enabled()} disabled={state() !== "ready"} as="span" onChange={() => void toggle()} />
    </label>
    <p class="software-detail-hint">{uiText.softwareDetail.protection.hint}</p>
    <Show when={deciding()}>
      {(entry) => <p class="software-detail-hint software-protection-source">{protectionSourceLine(entry())}</p>}
    </Show>
    <Show when={state() === "error"}><p class="software-protection-message error" role="alert">{uiText.softwareDetail.protection.failed}</p></Show>
    <Show when={state() === "loading" || state() === "saving"}><p class="software-protection-message" role="status">{uiText.softwareDetail.processing}</p></Show>
  </div>;
}

/** 来源一行：条目的来源和状态；用户条目替换了默认时，说默认是什么、用户在哪里改成了什么。不认识的来源原样显示。 */
function protectionSourceLine(entry: ProtectedOptimizationTarget) {
  const copy = uiText.softwareDetail.protection;
  const source = sourceName(entry.protectionSource);
  const state = stateName(entry.protectionLevel);
  return entry.defaultProtectionSource != null && entry.defaultProtectionLevel != null
    ? copy.replacedSourceLine(sourceName(entry.defaultProtectionSource), stateName(entry.defaultProtectionLevel), source, state)
    : copy.sourceLine(source, state);
}

function sourceName(source: string) {
  return isKnownProtectionSource(source) ? uiText.softwareDetail.protection.sources[source] : source;
}

function stateName(level: number) {
  const copy = uiText.softwareDetail.protection.state;
  return normalizedLevel(level) > 0 ? copy.protected : copy.unprotected;
}

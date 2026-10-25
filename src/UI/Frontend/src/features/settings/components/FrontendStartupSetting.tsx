import { Show, createSignal, onMount } from "solid-js";
import type { SettingsTextBundle } from "../../../text.ts";
import { requestLoginStartup } from "../shellLoginStartup.ts";

/**
 * 当前用户的登录启动项，由原生壳读写。
 * confirmed 是最近一次原生壳确认的状态（null 表示还没有读到）；
 * 写入失败时保留它并重新读取一次，开关显示读回的实际状态；读取失败时开关不可用，直到重试读到状态。
 */
export function FrontendStartupSetting(props: { text: SettingsTextBundle["systemIntegration"] }) {
  const [confirmed, setConfirmed] = createSignal<boolean | null>(null);
  const [phase, setPhase] = createSignal<"reading" | "writing" | "idle">("reading");
  const [failure, setFailure] = createSignal<"read" | "write" | null>(null);
  const [lastRequested, setLastRequested] = createSignal<boolean | null>(null);
  let toggle: HTMLInputElement | undefined;
  const read = async () => {
    setPhase("reading");
    try {
      setConfirmed(await requestLoginStartup());
    } catch {
      setConfirmed(null);
      setFailure("read");
    } finally {
      setPhase("idle");
    }
  };
  const retryRead = async () => {
    setFailure(null);
    await read();
  };
  const write = async (value: boolean) => {
    setLastRequested(value);
    setFailure(null);
    setPhase("writing");
    try {
      setConfirmed(await requestLoginStartup(value));
      setPhase("idle");
    } catch {
      // 写入失败：重新读取一次（读取失败会改为 read 失败），开关同步成读回的状态。
      setFailure("write");
      await read();
      if (toggle) toggle.checked = confirmed() === true;
    }
  };
  onMount(() => { void read(); });
  const statusText = () => {
    if (phase() === "reading" && confirmed() === null) return props.text.loginStartLoading;
    if (failure() === "write") return props.text.loginStartWriteFailed;
    if (failure() === "read") return props.text.loginStartUnavailable;
    return null;
  };
  const retry = () => {
    const value = lastRequested();
    if (failure() === "write" && value !== null) void write(value);
    else void retryRead();
  };
  return <div class="settings-row" aria-busy={phase() !== "idle"}>
    <div class="settings-row-copy">
      <strong>{props.text.loginStartTitle}</strong>
      <span>{props.text.loginStartDescription}</span>
      <Show when={statusText()}>{(text) => <span role="status">{text()}</span>}</Show>
      <Show when={failure() !== null && phase() === "idle"}>
        <button type="button" class="secondary" onClick={retry}>{props.text.loginStartRetry}</button>
      </Show>
    </div>
    <label class="settings-switch">
      <input type="checkbox" ref={(element) => { toggle = element; }} aria-label={props.text.loginStartTitle}
        checked={confirmed() === true} disabled={phase() !== "idle" || confirmed() === null}
        onChange={(event) => { void write(event.currentTarget.checked); }} />
      <span />
    </label>
  </div>;
}

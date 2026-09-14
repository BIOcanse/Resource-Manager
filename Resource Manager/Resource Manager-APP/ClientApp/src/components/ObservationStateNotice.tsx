import { Show, type JSX } from "solid-js";
import {
  observationCanRender,
  type ObservationState
} from "../observation/observationState";
import { formatObservationTimestamp } from "../presentation/observationTimestamp.ts";

interface ObservationStateNoticeProps {
  state: ObservationState;
  label: string;
  profileDisabledMessage?: string;
  onRetry?: () => void;
  presentation?: "all" | "blocking-only";
}

interface ObservationStateBoundaryProps extends ObservationStateNoticeProps {
  children: JSX.Element;
  renderWhenUnavailable?: boolean;
}

export function ObservationStateNotice(props: ObservationStateNoticeProps) {
  const visible = () => props.presentation === "blocking-only"
    ? props.state.status === "error" || props.state.status === "profile-disabled"
    : props.state.status !== "ready";
  const title = () => {
    switch (props.state.status) {
      case "loading":
        return `正在加载${props.label}`;
      case "stale":
        return `正在显示最近一次${props.label}`;
      case "error":
        return `${props.label}暂不可用`;
      case "profile-disabled":
        return `${props.label}在当前启动配置中不可用`;
      default:
        return "";
    }
  };
  const detail = () => {
    if (props.state.status === "profile-disabled") {
      return props.profileDisabledMessage
        ?? props.state.lastError
        ?? "当前启动配置未提供该数据源。";
    }
    if (props.state.lastError) {
      return props.state.lastError;
    }
    if (props.state.status === "stale") {
      return "实时刷新失败，以下内容不是当前状态。";
    }
    return "";
  };
  const capturedAt = () => formatObservationTimestamp(props.state.capturedAt);

  return (
    <Show when={visible()}>
      <div
        class={`observation-state ${props.state.status}`}
        role={props.state.status === "error" ? "alert" : "status"}
        aria-live="polite"
      >
        <strong>{title()}</strong>
        <Show when={capturedAt()}>
          {(value) => <span>采集于 {value()}</span>}
        </Show>
        <Show when={detail()}>
          {(value) => <span>{value()}</span>}
        </Show>
        <Show when={props.state.status === "error" && props.onRetry}>
          <button class="secondary" type="button" onClick={() => props.onRetry?.()}>
            重试
          </button>
        </Show>
      </div>
    </Show>
  );
}

export function ObservationStateBoundary(props: ObservationStateBoundaryProps) {
  return (
    <>
      <ObservationStateNotice
        state={props.state}
        label={props.label}
        profileDisabledMessage={props.profileDisabledMessage}
        onRetry={props.onRetry}
        presentation={props.presentation}
      />
      <Show when={observationCanRender(props.state) || props.renderWhenUnavailable === true}>
        {props.children}
      </Show>
    </>
  );
}

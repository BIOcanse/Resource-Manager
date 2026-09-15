import { Show, type JSX } from "solid-js";
import {
  observationCanRender,
  type ObservationState
} from "../observation/observationState";
import { formatObservationTimestamp } from "../presentation/observationTimestamp.ts";
import { uiText } from "../text.ts";

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
        return uiText.observationNotice.loading(props.label);
      case "stale":
        return uiText.observationNotice.stale(props.label);
      case "error":
        return uiText.observationNotice.unavailable(props.label);
      case "profile-disabled":
        return uiText.observationNotice.profileDisabled(props.label);
      default:
        return "";
    }
  };
  const detail = () => {
    if (props.state.status === "profile-disabled") {
      return props.profileDisabledMessage
        ?? props.state.lastError
        ?? uiText.observationNotice.profileDisabledDetail;
    }
    if (props.state.lastError) {
      return props.state.lastError;
    }
    if (props.state.status === "stale") {
      return uiText.observationNotice.staleDetail;
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
          {(value) => <span>{uiText.observationNotice.capturedAt(value())}</span>}
        </Show>
        <Show when={detail()}>
          {(value) => <span>{value()}</span>}
        </Show>
        <Show when={props.state.status === "error" && props.onRetry}>
          <button class="secondary" type="button" onClick={() => props.onRetry?.()}>
            {uiText.observationNotice.retry}
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

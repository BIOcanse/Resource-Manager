import { CircleAlert, Clock3, Inbox } from "lucide-solid";
import type { JSX } from "solid-js";

export type ContentStateKind = "empty" | "waiting" | "unavailable";

export function ContentState(props: {
  readonly kind: ContentStateKind;
  readonly title: string;
  readonly detail?: string;
  readonly actions?: JSX.Element;
}) {
  return (
    <div
      class={`content-state ${props.kind}`}
      role={props.kind === "unavailable" ? "alert" : "status"}
      aria-live="polite"
    >
      <span class="content-state-icon" aria-hidden="true">
        {props.kind === "unavailable"
          ? <CircleAlert size={21} />
          : props.kind === "waiting"
            ? <Clock3 size={21} />
            : <Inbox size={21} />}
      </span>
      <div class="content-state-copy">
        <strong>{props.title}</strong>
        {props.detail ? <p>{props.detail}</p> : null}
      </div>
      {props.actions ? <div class="content-state-actions">{props.actions}</div> : null}
    </div>
  );
}

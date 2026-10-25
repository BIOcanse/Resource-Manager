import { Show } from "solid-js";
import { currentSettingsText } from "../../../i18n/appTextStore.ts";
import { useAccessRequestSnapshot } from "../../trustedAccess/AccessRequestSnapshotContext.tsx";

export function TrustedAccessPendingBanner(props: {
  readonly hidden: () => boolean;
  readonly onReview: () => void;
}) {
  const snapshot = useAccessRequestSnapshot();
  const copy = () => currentSettingsText().systemIntegration.trustedAccess;
  const requests = () => snapshot()?.data?.requests ?? [];

  return (
    <Show when={!props.hidden() && requests().length > 0}>
      <aside class="trusted-access-notification" role="region" aria-label={copy().requestsTitle} aria-live="polite">
        <div class="trusted-access-notification-copy">
          <strong>{copy().requestsTitle}</strong>
          <span>{copy().requestsDescription}</span>
          <span>{copy().requestName}: {requests()[0]?.name}</span>
        </div>
        <span class="trusted-access-notification-count" aria-label={String(requests().length)}>
          {requests().length}
        </span>
        <button type="button" onClick={props.onReview}>{copy().reviewRequests}</button>
      </aside>
    </Show>
  );
}

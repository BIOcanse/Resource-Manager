import { Show } from "solid-js";
import { currentSettingsText } from "../../../i18n/appTextStore.ts";
import { useAccessRequestSnapshot } from "../../trustedAccess/AccessRequestSnapshotContext.tsx";
import { accessRequestListView } from "../../trustedAccess/trustedAccessPresentation.ts";

export function TrustedAccessPendingBanner(props: {
  readonly hidden: () => boolean;
  readonly onReview: () => void;
}) {
  const snapshot = useAccessRequestSnapshot();
  const copy = () => currentSettingsText().systemIntegration.trustedAccess;
  const requests = () => accessRequestListView(snapshot()).requests;
  const visible = () => !props.hidden() && requests().length > 0;

  return (
    <>
      {/* 播报区常驻在页面里，只更新文字；读屏才会播报新到的请求。 */}
      <p class="sr-only" aria-live="polite">
        {visible() ? copy().requestCount(requests().length) : ""}
      </p>
      <Show when={visible()}>
        <aside class="trusted-access-notification" role="region" aria-label={copy().requestsTitle}>
          <div class="trusted-access-notification-copy">
            <strong>{copy().requestsTitle}</strong>
            <span>{copy().requestsDescription}</span>
            <span>{copy().requestNamed(requests()[0]?.name ?? "")}</span>
          </div>
          <span class="trusted-access-notification-count count-badge" data-full-content={copy().requestCount(requests().length)}>
            <span aria-hidden="true">{requests().length}</span>
            <span class="sr-only">{copy().requestCount(requests().length)}</span>
          </span>
          <button type="button" onClick={props.onReview}>{copy().reviewRequests}</button>
        </aside>
      </Show>
    </>
  );
}

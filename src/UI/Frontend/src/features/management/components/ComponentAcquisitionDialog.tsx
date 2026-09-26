import { For, Show, createSignal } from "solid-js";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../../../ui/primitives/Dialog";
import { uiText } from "../../../text.ts";
import type { ComponentVersionOption, ManagedComponent } from "../../../types";

/**
 * 组件获取对话框：把外部厂商条款和版本选择摆在同一个地方，让用户能先读条款再决定。
 *
 * - 条款与来源页是可点开的链接，点开后对话框仍然在，回来才能同意。
 * - 版本是显式选择：已验证版本（我们固定并验证过的）或最新版本（上游当前发布）。
 * - 最新版本解析失败时该项保留并显示原因，不会悄悄消失，也不影响安装已验证版本。
 */
export interface ComponentAcquisitionRequest {
  component: ManagedComponent;
  /** 空数组表示这个来源没有版本可选（直链或手动获取）。 */
  versions: ComponentVersionOption[];
  /** 版本正在解析中。 */
  loading: boolean;
  /** 同意后要做的事是引导手动获取，而不是下载安装。 */
  manual: boolean;
}

export function ComponentAcquisitionDialog(props: {
  request: ComponentAcquisitionRequest | null;
  onOpenLink: (url: string) => void;
  onCancel: () => void;
  onConfirm: (versionChoice: string | null) => void;
}) {
  const [choice, setChoice] = createSignal<string | null>(null);
  let cancelButton: HTMLButtonElement | undefined;

  // 只有目录声明「需要确认外部厂商条款」的组件才显示条款与同意口径；
  // 其余组件走同一个弹窗，但只选版本。
  function requiresTerms(request: ComponentAcquisitionRequest) {
    return request.component.definition?.requiresExternalTermsAcknowledgement === true;
  }

  function selectableVersions(request: ComponentAcquisitionRequest) {
    return request.versions.filter((option) => option.available);
  }

  function effectiveChoice(request: ComponentAcquisitionRequest) {
    const selectable = selectableVersions(request);
    if (selectable.length === 0) {
      return null;
    }

    const current = choice();
    return current && selectable.some((option) => option.choice === current)
      ? current
      : selectable[0].choice;
  }

  function versionLabel(option: ComponentVersionOption) {
    const name = option.choice === "verified"
      ? uiText.componentAcquisition.verifiedVersion
      : uiText.componentAcquisition.latestVersion;
    return option.version ? `${name} · ${option.version}` : name;
  }

  return (
    <Show keyed when={props.request}>
      {(request) => (
        <DialogRoot
          open
          labelledBy="componentAcquisitionTitle"
          describedBy="componentAcquisitionDescription"
          backdropClass="feedback-backdrop"
          class="feedback-modal warning"
          dismissOnBackdrop={false}
          initialFocus={() => cancelButton}
          onDismiss={props.onCancel}
        >
          <DialogHeader
            title={request.component.definition?.name ?? uiText.stores.fallbackComponentName}
            titleId="componentAcquisitionTitle"
            closeLabel={uiText.feedback.close}
            onDismiss={props.onCancel}
          />
          <DialogBody class="feedback-body">
            <div id="componentAcquisitionDescription" class="feedback-description">
              <p>
                {requiresTerms(request)
                  ? request.manual
                    ? uiText.componentAcquisition.manualIntro
                    : uiText.componentAcquisition.installIntro
                  : request.manual
                    ? uiText.componentAcquisition.manualIntroWithoutTerms
                    : uiText.componentAcquisition.installIntroWithoutTerms}
              </p>

              <Show when={request.component.definition?.vendor}>
                {(vendor) => (
                  <p class="component-acquisition-vendor">
                    {uiText.componentAcquisition.vendor(vendor())}
                  </p>
                )}
              </Show>

              <div class="component-acquisition-links">
                <Show when={requiresTerms(request) && request.component.definition?.externalTermsUrl}>
                  {(url) => (
                    <button type="button" class="secondary" onClick={() => props.onOpenLink(url())}>
                      {uiText.componentAcquisition.openTerms}
                    </button>
                  )}
                </Show>
                <Show when={request.component.definition?.sourcePageUrl}>
                  {(url) => (
                    <button type="button" class="secondary" onClick={() => props.onOpenLink(url())}>
                      {uiText.componentAcquisition.openSourcePage}
                    </button>
                  )}
                </Show>
              </div>

              <Show when={!request.manual}>
                <Show
                  when={!request.loading}
                  fallback={<p>{uiText.componentAcquisition.loadingVersions}</p>}
                >
                  <Show when={request.versions.length > 0}>
                    <fieldset class="component-acquisition-versions">
                      <legend>{uiText.componentAcquisition.versionLegend}</legend>
                      <For each={request.versions}>
                        {(option) => (
                          <label
                            class="component-acquisition-version"
                            aria-disabled={!option.available}
                          >
                            <input
                              type="radio"
                              name="componentAcquisitionVersion"
                              value={option.choice}
                              disabled={!option.available}
                              checked={effectiveChoice(request) === option.choice}
                              onChange={() => setChoice(option.choice)}
                            />
                            <span>{versionLabel(option)}</span>
                            <Show when={option.choice === "verified"}>
                              <small>{uiText.componentAcquisition.verifiedHint}</small>
                            </Show>
                            <Show when={!option.available && option.unavailableReason}>
                              {(reason) => <small>{reason()}</small>}
                            </Show>
                          </label>
                        )}
                      </For>
                    </fieldset>
                  </Show>
                </Show>
              </Show>

              <Show when={request.component.definition?.requiresElevation}>
                <p>{uiText.componentAcquisition.elevationNote}</p>
              </Show>
            </div>
          </DialogBody>
          <DialogActions>
            <button
              ref={(element) => { cancelButton = element; }}
              class="secondary"
              type="button"
              onClick={props.onCancel}
            >
              {uiText.feedback.cancel}
            </button>
            <button
              type="button"
              disabled={!request.manual && request.loading}
              onClick={() => props.onConfirm(effectiveChoice(request))}
            >
              {requiresTerms(request)
                ? request.manual
                  ? uiText.componentAcquisition.agreeAndOpen
                  : uiText.componentAcquisition.agreeAndInstall
                : request.manual
                  ? uiText.componentAcquisition.openDownloadPage
                  : uiText.componentAcquisition.startInstall}
            </button>
          </DialogActions>
        </DialogRoot>
      )}
    </Show>
  );
}

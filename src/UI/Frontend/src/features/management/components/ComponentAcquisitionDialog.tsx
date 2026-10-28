import { componentDisplayName } from "../../../presentation/userFacingText.ts";
import { componentNeedsUpdate, componentVersionFailure } from "../../../presentation/componentPresentation.ts";
import { renderBackendMessage } from "../../../presentation/backendMessage.ts";
import { Show, createSignal } from "solid-js";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../../../ui/primitives/Dialog";
import { currentSettingsText, uiText } from "../../../text.ts";
import { VersionSelector, VersionSelectorPlaceholder } from "../../settings/components/VersionSelector";
import type { ComponentVersionOption, ManagedComponent } from "../../../types";

/**
 * 组件获取对话框：把外部厂商条款和版本选择摆在同一个地方，让用户能先读条款再决定。
 *
 * - 条款与来源页是可点开的链接，点开后对话框仍然在，回来才能同意。
 * - 版本是显式选择；历史发布按系列折叠，无法可靠排序的版本不能选。
 */
export interface ComponentAcquisitionRequest {
  component: ManagedComponent;
  /** 空数组表示这个来源没有版本可选（直链或手动获取）。 */
  versions: ComponentVersionOption[];
  /** 版本正在解析中。 */
  loading: boolean;
  catalogStatus?: string;
  complete?: boolean;
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

  function isUpdate(request: ComponentAcquisitionRequest) {
    return componentNeedsUpdate(request.component);
  }

  function selectableVersions(request: ComponentAcquisitionRequest) {
    return isUpdate(request) || request.component.installed
      ? []
      : request.versions.filter((option) => option.available);
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

  const catalogMessage = (status: string) => {
    if (status === "unavailable") return uiText.componentAcquisition.catalogUnavailable;
    if (status === "partial") return uiText.componentAcquisition.catalogPartial;
    return uiText.componentAcquisition.catalogError;
  };

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
            title={componentDisplayName(request.component.definition?.id)}
            titleId="componentAcquisitionTitle"
            closeLabel={uiText.feedback.close}
            onDismiss={props.onCancel}
          />
          <DialogBody class="feedback-body">
            <div id="componentAcquisitionDescription" class="feedback-description">
              <p>
                {isUpdate(request)
                  ? renderBackendMessage(request.component.message)
                  : requiresTerms(request)
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

              <Show when={!request.manual && !isUpdate(request)}>
                <Show
                  when={!request.loading}
                  fallback={
                    <fieldset class="component-acquisition-versions">
                      <legend>{uiText.componentAcquisition.versionLegend}</legend>
                      <p class="sr-only" role="status">{uiText.componentAcquisition.loadingVersions}</p>
                      <VersionSelectorPlaceholder text={currentSettingsText().updates} />
                    </fieldset>
                  }
                >
                  <Show when={request.versions.length > 0}>
                    <fieldset class="component-acquisition-versions">
                      <legend>{uiText.componentAcquisition.versionLegend}</legend>
                      <VersionSelector options={request.versions.map(option => ({ ...option, unavailableReason: componentVersionFailure(option.unavailableReasonCode) }))}
                        choice={effectiveChoice(request)} onSelect={setChoice}
                        text={currentSettingsText().updates}
                        installedVersionUnknown={request.component.installed === true} />
                    </fieldset>
                  </Show>
                </Show>
                <Show when={!isUpdate(request) && (request.catalogStatus === "error" || request.catalogStatus === "partial" || request.catalogStatus === "unavailable")}>
                  <p role="status">{catalogMessage(request.catalogStatus!)}</p>
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
              disabled={!request.manual && (request.loading ||
                (request.versions.length > 0 && effectiveChoice(request) === null))}
              onClick={() => props.onConfirm(effectiveChoice(request))}
            >
              {requiresTerms(request)
                ? request.manual
                  ? uiText.componentAcquisition.agreeAndOpen
                  : isUpdate(request)
                    ? uiText.componentAcquisition.agreeAndUpdate
                    : uiText.componentAcquisition.agreeAndInstall
                : request.manual
                  ? uiText.componentAcquisition.openDownloadPage
                  : isUpdate(request)
                    ? uiText.componentCatalog.update
                    : uiText.componentAcquisition.startInstall}
            </button>
          </DialogActions>
        </DialogRoot>
      )}
    </Show>
  );
}

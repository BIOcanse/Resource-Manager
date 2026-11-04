import { uiText } from "../../../text.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppSettings } from "../../../types.ts";
import { ControlAccessLevelSetting } from "./ControlAccessLevelSetting";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { NumberField } from "./SettingsControls";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

/** Keepalive and interval use the shared draft; access level applies immediately. Keep the interval visible but disabled when keepalive is off to preserve layout. */
export function HardwareControlSettings(props: {
  control: NonNullable<AppSettings["control"]>;
  text: SettingsTextBundle;
  onChange: (control: NonNullable<AppSettings["control"]>) => void;
}) {
  // Visibility owns access-level reads; release while hidden and reread on return.
  const workId = frontendWorkIds.settingsHardwareControl;
  useFrontendVisibilityDemand(workId, [workId]);
  return <div class="settings-section-panel" {...frontendVisibilitySurface(workId, [workId])}>
    <div class="settings-row">
      <div class="settings-row-copy">
        <div class="settings-row-title">
          <strong>{props.text.hardwareControl.keepAliveTitle}</strong>
          <HelpHint label={props.text.hardwareControl.keepAliveTitle}>{props.text.hardwareControl.keepAliveDescription}</HelpHint>
        </div>
      </div>
      <label class="settings-switch">
        <input type="checkbox" aria-label={props.text.hardwareControl.keepAliveTitle}
          checked={props.control.keepAlive}
          onChange={(event) => props.onChange({ ...props.control, keepAlive: event.currentTarget.checked })} />
        <span />
      </label>
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <div class="settings-row-title">
          <strong>{props.text.hardwareControl.reassertionTitle}</strong>
          <HelpHint label={props.text.hardwareControl.reassertionTitle}>{props.text.hardwareControl.reassertionDescription}</HelpHint>
        </div>
      </div>
      <NumberField label={uiText.control.reassertionInterval}
        value={props.control.reassertionIntervalSeconds}
        min={1} max={86400} step={1}
        disabled={!props.control.keepAlive}
        onChange={reassertionIntervalSeconds => props.onChange({ ...props.control, reassertionIntervalSeconds })} />
    </div>
    <ControlAccessLevelSetting />
  </div>;
}

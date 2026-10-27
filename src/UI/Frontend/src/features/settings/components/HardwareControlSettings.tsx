import { uiText } from "../../../text.ts";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppSettings } from "../../../types.ts";
import { ControlAccessLevelSetting } from "./ControlAccessLevelSetting";
import { NumberField } from "./SettingsControls";

/**
 * 设置 → 硬件控制：控制页那些调频调压项的重新应用间隔和调节权限档位。
 * 间隔随设置页草稿保存；调节权限档位不走草稿，选完就生效（见 ControlAccessLevelSetting）。
 */
export function HardwareControlSettings(props: {
  control: NonNullable<AppSettings["control"]>;
  text: SettingsTextBundle;
  onChange: (control: NonNullable<AppSettings["control"]>) => void;
}) {
  return <div class="settings-section-panel">
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{props.text.hardwareControl.reassertionTitle}</strong>
        <span>{props.text.hardwareControl.reassertionDescription}</span>
      </div>
      <NumberField label={uiText.control.reassertionInterval}
        value={props.control.reassertionIntervalSeconds}
        min={1} max={86400} step={1}
        onChange={reassertionIntervalSeconds => props.onChange({ ...props.control, reassertionIntervalSeconds })} />
    </div>
    <ControlAccessLevelSetting />
  </div>;
}

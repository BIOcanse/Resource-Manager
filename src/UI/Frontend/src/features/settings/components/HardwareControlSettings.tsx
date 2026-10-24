import { uiText } from "../../../text.ts";
import type { AppSettings } from "../../../types.ts";
import { NumberField } from "./SettingsControls";

export function HardwareControlSettings(props: {
  control: NonNullable<AppSettings["control"]>;
  onChange: (control: NonNullable<AppSettings["control"]>) => void;
}) {
  return <div class="settings-section">
    <NumberField label={uiText.control.reassertionInterval} value={props.control.reassertionIntervalSeconds}
      min={1} max={86400} step={1}
      onChange={reassertionIntervalSeconds => props.onChange({ ...props.control, reassertionIntervalSeconds })} />
  </div>;
}

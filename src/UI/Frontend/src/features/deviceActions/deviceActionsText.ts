import { deviceRestartReasonName, deviceResultName } from "../../data/deviceActions/deviceActions.ts";
import { uiText } from "../../text.ts";

// Localize known device-action codes; display the number for unknown codes.

export function deviceRestartReasonText(code: number): string {
  const name = deviceRestartReasonName(code);
  return name === null ? uiText.deviceActions.unknownCode(String(code)) : uiText.deviceActions.reasons[name];
}

export function deviceResultText(code: number): string {
  const name = deviceResultName(code);
  return name === null ? uiText.deviceActions.unknownCode(String(code)) : uiText.deviceActions.results[name];
}

/** Show nonzero Windows error codes in hexadecimal. */
export function deviceWin32DetailText(detail: number): string | null {
  return detail === 0 ? null : `0x${detail.toString(16).toUpperCase().padStart(8, "0")}`;
}

import { createMemo } from "solid-js";
import { uiText } from "../../text.ts";
import { curveStops } from "./fanCurve.ts";
import { FixedAxisCurveChart } from "./FixedAxisCurveChart.tsx";
const ticks = Array.from({ length: 11 }, (_, index) => index * 10);
/** Original fan behavior, through the common fixed-axis renderer. */
export function FanCurveChart(props: {
  stops: readonly number[]; temperatures?: readonly number[];
  disabled?: boolean; onChange: (stops: number[]) => void;
}) {
  const temperatures = () => props.temperatures ?? curveStops;
  const range = createMemo(() => temperatures().reduce((range, temperature) => ({
    minimum: Math.min(range.minimum, temperature), maximum: Math.max(range.maximum, temperature)
  }), { minimum: 0, maximum: 100 }));
  return <FixedAxisCurveChart xs={temperatures()} values={props.stops} disabled={props.disabled}
    minimumX={range().minimum} maximumX={range().maximum} minimumY={0} maximumY={100}
    xTicks={ticks.map(percent => Number((range().minimum + percent / 100 * (range().maximum - range().minimum)).toFixed(1)))}
    yTicks={ticks} xUnit="°C" yUnit="%" step={1} coarseStep={10} nearestPoint
    label={uiText.control.curvePreview} pointLabel={(x, value) => uiText.control.curvePointName(x, value)}
    valueLabel={value => uiText.control.curvePointValue(value)}
    onChange={values => props.onChange(values.map(value => Math.round(Math.min(100, Math.max(0, value)))))} />;
}

import { createMemo, Index, Show } from "solid-js";
import type { DeviceTopologyDisplayConnection } from "../../../types.ts";
import type { UserDetailItem } from "../../../presentation/userDetails.ts";
import { uiText } from "../../../text.ts";
import { buildDisplayEdidView } from "../displayEdidPresentation.ts";

function Rows(props: { values: UserDetailItem[] }) {
  return <dl><Index each={props.values}>{row => <div class="user-details-row"><dt>{row().label}</dt><dd>{row().value}</dd></div>}</Index></dl>;
}
function Declarations(props: { title: string; values: { title: string; rows: UserDetailItem[] }[] }) {
  return <section class="user-details-section">
    <h3>{props.title}</h3>
    <Show when={props.values.length > 0} fallback={<p>{uiText.deviceTopology.edid.empty}</p>}>
      <Index each={props.values}>{value => <details><summary>{value().title}</summary><Rows values={value().rows} /></details>}</Index>
    </Show>
  </section>;
}
/** Same selected port/display object as the original dialog; no additional reads or identity lookup. */
export function DisplayEdidDetails(props: { display: DeviceTopologyDisplayConnection }) {
  const value = createMemo(() => buildDisplayEdidView(props.display));
  return <div class="device-display-declarations">
    <section class="user-details-section" data-display-current>
      <h3>{uiText.deviceTopology.edid.current}</h3><Rows values={value().current} />
    </section>
    <section class="user-details-section" data-display-edid>
      <h3>{uiText.deviceTopology.edid.declared}</h3><p>{uiText.deviceTopology.edid.hint}</p>
      <Show when={value().available} fallback={<p>{uiText.deviceTopology.edid.unavailable}</p>}>
        <Rows values={value().declaration} />
        <Declarations title={uiText.deviceTopology.edid.modes} values={value().modes} />
        <Declarations title={uiText.deviceTopology.edid.ranges} values={value().ranges} />
        <Show when={value().selectionsReported} fallback={<p>{uiText.deviceTopology.edid.unavailable}</p>}>
          <Declarations title={uiText.deviceTopology.edid.selections} values={value().selections} />
        </Show>
      </Show>
    </section>
    <details class="user-details-section" data-display-edid-raw>
      <summary>{uiText.deviceTopology.edid.raw}</summary>
      <dl><div class="user-details-row"><dt>Base64</dt><dd><code>{value().raw}</code></dd></div></dl>
    </details>
  </div>;
}

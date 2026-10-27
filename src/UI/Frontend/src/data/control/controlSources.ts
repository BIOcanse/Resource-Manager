import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import type { SourceRegistry } from "../../frontendRuntime/source/SourceRegistry.ts";
import {
  getControlObjects, getControlState, getControlInstances, getControlPresets,
  getControlOverclockConsent, getControlAccessLevel, getControlNoticeAcknowledged
} from "../../features/control/controlApi.ts";

/** Control reads share the runtime lifetime; page demand only controls sampling. */
export function createControlSources(registry: SourceRegistry, client: RequestClient) {
  return {
    objects: registry.define({ key: "control.objects", load: signal => getControlObjects(client, signal), retainLastGood: false }),
    state: registry.define({ key: "control.state", load: signal => getControlState(client, signal), retainLastGood: false }),
    instances: registry.define({ key: "control.instances", load: signal => getControlInstances(client, signal), retainLastGood: false }),
    presets: registry.define({ key: "control.presets", load: signal => getControlPresets(client, signal), retainLastGood: false }),
    overclockConsent: registry.define({ key: "control.overclockConsent", load: signal => getControlOverclockConsent(client, signal), retainLastGood: false }),
    accessLevel: registry.define({ key: "control.accessLevel", load: signal => getControlAccessLevel(client, signal), retainLastGood: false }),
    notice: registry.define({ key: "control.notice", load: signal => getControlNoticeAcknowledged(client, signal), retainLastGood: false })
  };
}

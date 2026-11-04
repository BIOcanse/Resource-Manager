import {
  requireArray, requireNonEmptyString, requireNullable, requireOneOf, requireRecord, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { DeviceTopologyExternalMachine } from "../../types.ts";

export function decodeExternalMachines(value: unknown, path: string): DeviceTopologyExternalMachine[] | null {
  return requireNullable(value, path, (items, itemsPath) => {
    const ids = new Set<string>();
    return requireArray(items, itemsPath).map((item, index) => {
      const itemPath = `${itemsPath}[${index}]`;
      const record = requireRecord(item, itemPath);
      const localLinkDeviceId = requireNonEmptyString(record.localLinkDeviceId, `${itemPath}.localLinkDeviceId`);
      if (ids.has(localLinkDeviceId)) throw new ResponseDecodeError(`${itemPath}.localLinkDeviceId`, "unique local link id");
      ids.add(localLinkDeviceId);
      const componentsPath = `${itemPath}.components`;
      const components = requireArray(record.components, componentsPath);
      if (components.length !== 1) throw new ResponseDecodeError(componentsPath, "exactly one SoC component");
      const componentPath = `${componentsPath}[0]`;
      const component = requireRecord(components[0], componentPath);
      const evidencePath = `${itemPath}.evidence`;
      const evidence = requireRecord(record.evidence, evidencePath);
      if (evidence.mediaConnectStateRaw !== 1) throw new ResponseDecodeError(`${evidencePath}.mediaConnectStateRaw`, "connected state 1");
      return {
        localLinkDeviceId,
        peerIdentity: requireNullable(record.peerIdentity, `${itemPath}.peerIdentity`, requireNonEmptyString),
        formFactor: requireOneOf(record.formFactor, `${itemPath}.formFactor`, ["workstation"] as const),
        model: requireNullable(record.model, `${itemPath}.model`, requireNonEmptyString),
        components: [{
          component: requireOneOf(component.component, `${componentPath}.component`, ["soc"] as const),
          model: requireNullable(component.model, `${componentPath}.model`, requireNonEmptyString)
        }],
        evidence: {
          source: requireOneOf(evidence.source, `${evidencePath}.source`, ["windows-usb4net"] as const),
          compatibleId: requireOneOf(evidence.compatibleId, `${evidencePath}.compatibleId`, ["USB4\\USB4NET"] as const),
          mediaConnectStateRaw: 1 as const,
          peerPropertiesReason: requireNonEmptyString(evidence.peerPropertiesReason, `${evidencePath}.peerPropertiesReason`)
        }
      };
    });
  });
}

import {
  capabilityLabels,
  connectionFacts,
  deviceRevision,
  deviceTitle,
  displayValue,
  internalDeviceFacts,
  interfaceProtocols,
  joinSummary,
  summaryFields,
  usbSpecification
} from "./adapterEvidence.ts";
import type { DeviceAdapter, GenericDeviceModel } from "./types";
import { uiText } from "../../text.ts";

export const genericDeviceAdapter: DeviceAdapter<GenericDeviceModel> = {
  id: "generic-device",
  matches: () => true,
  createModel: (context) => {
    const title = deviceTitle(context);
    const connection = connectionFacts(context);
    const protocols = interfaceProtocols(context.port);
    const deviceClass = displayValue(context.port.usb?.deviceClass, context.port.hardwareKind);
    const specification = usbSpecification(context.port);
    const revision = deviceRevision(context.port);
    const internal = context.scope === "internal";
    const facts = internalDeviceFacts(context);
    const typeLabel = resolveGenericType(context.port, internal);
    return {
      adapterId: "generic-device",
      kind: "generic-device",
      deviceTypeLabel: typeLabel,
      title,
      subtitle: internal
        ? joinSummary(typeLabel, facts?.transport)
        : joinSummary(deviceClass, connection.currentLink),
      badge: typeLabel,
      iconKind: "device",
      ...connection,
      deviceClass,
      usbSpecification: specification,
      deviceRevision: revision,
      interfaceProtocols: protocols,
      summaryFields: internal
        ? summaryFields(
          [uiText.deviceAdapters.label.deviceType, typeLabel],
          [uiText.deviceAdapters.label.internalTransport, facts?.transport],
          [uiText.deviceAdapters.label.driverService, facts?.driverService],
          [uiText.deviceAdapters.label.deviceStatus, facts?.deviceStatus]
        )
        : summaryFields(
          [uiText.deviceAdapters.label.deviceType, deviceClass],
          [uiText.deviceAdapters.label.upstreamInterface, connection.upstreamInterface],
          [uiText.deviceAdapters.label.usbSpecification, specification],
          [uiText.deviceAdapters.label.currentLink, connection.currentLink]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [typeLabel, internal ? uiText.deviceAdapters.internalDevice : uiText.deviceAdapters.externalDevice, "device", deviceClass, ...protocols]
    };
  }
};

function resolveGenericType(port: import("../../types").DeviceTopologyPort, internal: boolean) {
  if (!internal) return uiText.deviceAdapters.externalDevice;
  if (/Composite/i.test(`${port.displayName} ${port.usb?.deviceClass ?? ""}`)) return uiText.deviceAdapters.usbCompositeDevice;
  if (port.pnpClass?.toLocaleLowerCase() === "ports") return uiText.deviceAdapters.internalCommunicationPort;
  return uiText.deviceAdapters.internalDevice;
}

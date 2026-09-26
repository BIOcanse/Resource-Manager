import {
  activeCapabilities,
  capabilityLabels,
  connectionFacts,
  deviceTitle,
  displayValue,
  hasUsbInterface,
  internalDeviceFacts,
  interfaceProtocols,
  joinSummary,
  summaryFields
} from "./adapterEvidence.ts";
import type { AudioDeviceModel, DeviceAdapter } from "./types";
import { uiText } from "../../text.ts";

export const audioDeviceAdapter: DeviceAdapter<AudioDeviceModel> = {
  id: "audio-device",
  matches: ({ scope, port }) => hasUsbInterface(port, /Audio (?:Control|Streaming)|\[01\/(?:01|02)\//i)
    || (scope === "internal" && (port.busKind === "audio" || port.pnpClass?.toLocaleLowerCase() === "media")),
  createModel: (context) => {
    const internal = context.scope === "internal";
    const title = deviceTitle(context, internal ? uiText.deviceAdapters.internalAudioDevice : uiText.deviceAdapters.usbAudioDevice);
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const audioControl = protocols.some((value) => /Audio Control|\[01\/01\//i.test(value));
    const audioStreaming = protocols.some((value) => /Audio Streaming|\[01\/02\//i.test(value));
    const hidControls = protocols.some((value) => /HID|\[03\//i.test(value));
    const capabilities = activeCapabilities([
      [uiText.deviceAdapters.label.audioControl, audioControl],
      [uiText.deviceAdapters.label.audioStreaming, audioStreaming],
      [uiText.deviceAdapters.label.hidControls, hidControls]
    ]);
    const typeLabel = resolveAudioType(title, context);
    const summary = internal
      ? summaryFields(
        [uiText.deviceAdapters.label.audioRole, typeLabel],
        [uiText.deviceAdapters.label.internalTransport, facts?.transport],
        [uiText.deviceAdapters.label.driverService, facts?.driverService],
        [uiText.deviceAdapters.label.deviceStatus, facts?.deviceStatus]
      )
      : summaryFields(
        [uiText.deviceAdapters.label.deviceType, typeLabel],
        [uiText.deviceAdapters.label.upstreamInterface, connection.upstreamInterface],
        [uiText.deviceAdapters.label.audioCapability, capabilities.join(" / ")],
        [uiText.deviceAdapters.label.currentLink, connection.currentLink]
      );
    return {
      adapterId: "audio-device",
      kind: "audio-device",
      deviceTypeLabel: typeLabel,
      title,
      subtitle: internal
        ? joinSummary(typeLabel, facts?.transport)
        : joinSummary(capabilities.join(" / "), connection.currentLink),
      badge: typeLabel,
      iconKind: "headphones",
      ...connection,
      audioControl,
      audioStreaming,
      hidControls,
      interfaceProtocols: protocols,
      summaryFields: summary,
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [
        "音频",
        uiText.deviceAdapters.headset,
        "audio",
        typeLabel,
        displayValue(facts?.transport),
        displayValue(facts?.driverService),
        ...protocols
      ]
    };
  }
};

function resolveAudioType(title: string, context: import("./types").DeviceAdapterContext) {
  if (/headset|headphone|耳机/i.test(title)) return uiText.deviceAdapters.headset;
  if (/microphone|麦克风|话筒/i.test(title)) return uiText.deviceAdapters.microphone;
  if (/speaker|音箱|扬声器/i.test(title)) return uiText.deviceAdapters.speaker;
  if (/^BthA2dp$/i.test(context.port.service ?? "")) return uiText.deviceAdapters.bluetoothStereoAudio;
  if (/^BthHFAud$/i.test(context.port.service ?? "")) return uiText.deviceAdapters.bluetoothHandsFreeAudio;
  if (/^(NVHDA|AtiHDAudioService)$/i.test(context.port.service ?? "")) return uiText.deviceAdapters.displayAudioDevice;
  if (context.scope === "internal" && /^HDAUDIO\\/i.test(context.port.deviceId)) return uiText.deviceAdapters.internalAudioCodec;
  return context.scope === "internal" ? uiText.deviceAdapters.internalAudioDevice : uiText.deviceAdapters.usbAudioDevice;
}

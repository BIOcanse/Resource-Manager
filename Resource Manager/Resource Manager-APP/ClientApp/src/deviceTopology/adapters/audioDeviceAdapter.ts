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

export const audioDeviceAdapter: DeviceAdapter<AudioDeviceModel> = {
  id: "audio-device",
  matches: ({ scope, port }) => hasUsbInterface(port, /Audio (?:Control|Streaming)|\[01\/(?:01|02)\//i)
    || (scope === "internal" && (port.busKind === "audio" || port.pnpClass?.toLocaleLowerCase() === "media")),
  createModel: (context) => {
    const internal = context.scope === "internal";
    const title = deviceTitle(context, internal ? "内部音频设备" : "USB 音频设备");
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const audioControl = protocols.some((value) => /Audio Control|\[01\/01\//i.test(value));
    const audioStreaming = protocols.some((value) => /Audio Streaming|\[01\/02\//i.test(value));
    const hidControls = protocols.some((value) => /HID|\[03\//i.test(value));
    const capabilities = activeCapabilities([
      ["音频控制", audioControl],
      ["音频流", audioStreaming],
      ["HID 控制", hidControls]
    ]);
    const typeLabel = resolveAudioType(title, context);
    const summary = internal
      ? summaryFields(
        ["音频角色", typeLabel],
        ["内部传输", facts?.transport],
        ["驱动服务", facts?.driverService],
        ["设备状态", facts?.deviceStatus]
      )
      : summaryFields(
        ["设备类型", typeLabel],
        ["上游接口", connection.upstreamInterface],
        ["音频能力", capabilities.join(" / ")],
        ["当前链路", connection.currentLink]
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
        "耳机",
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
  if (/headset|headphone|耳机/i.test(title)) return "耳机";
  if (/microphone|麦克风|话筒/i.test(title)) return "麦克风";
  if (/speaker|音箱|扬声器/i.test(title)) return "音箱";
  if (/^BthA2dp$/i.test(context.port.service ?? "")) return "Bluetooth 立体声音频";
  if (/^BthHFAud$/i.test(context.port.service ?? "")) return "Bluetooth 免提音频";
  if (/^(NVHDA|AtiHDAudioService)$/i.test(context.port.service ?? "")) return "显示音频设备";
  if (context.scope === "internal" && /^HDAUDIO\\/i.test(context.port.deviceId)) return "内置音频编解码器";
  return context.scope === "internal" ? "内部音频设备" : "USB 音频设备";
}

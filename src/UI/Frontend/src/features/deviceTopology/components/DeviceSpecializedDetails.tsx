import { mountStateLabel } from "../deviceVocabulary.ts";
import { For, Match, Show, Switch } from "solid-js";
import type {
  AudioDeviceModel,
  CameraDeviceModel,
  DockDeviceModel,
  ExternalGpuDockDeviceModel,
  ExternalStorageDeviceModel,
  GenericDeviceModel,
  GraphicsAdapterDeviceModel,
  InternalControllerDeviceModel,
  KeyboardDeviceModel,
  MobileDeviceModel,
  MonitorDeviceModel,
  MouseDeviceModel,
  NetworkAdapterDeviceModel,
  BluetoothDeviceModel,
  PowerInputDeviceModel,
  SpecializedDeviceModel,
  UsbHubDeviceModel
} from "../adapters/types";
import { formatBytes } from "../adapters/adapterEvidence";
import "./specialized-device-details.css";
import { uiText } from "../../../text.ts";

export function DeviceSpecializedDetails(props: { model: SpecializedDeviceModel }) {
  return (
    <section class={`device-specialized-details kind-${props.model.kind}`} aria-label={uiText.deviceSpecialized.sectionLabel(props.model.deviceTypeLabel)}>
      <Switch>
        <Match when={asKind<MonitorDeviceModel>(props.model, "monitor")}>
          {(model) => <MonitorDetails model={model()} />}
        </Match>
        <Match when={asKind<ExternalGpuDockDeviceModel>(props.model, "external-gpu-dock")}>
          {(model) => <ExternalGpuDetails model={model()} />}
        </Match>
        <Match when={asKind<DockDeviceModel>(props.model, "dock")}>
          {(model) => <HubDetails model={model()} />}
        </Match>
        <Match when={asKind<UsbHubDeviceModel>(props.model, "usb-hub")}>
          {(model) => <HubDetails model={model()} />}
        </Match>
        <Match when={asKind<ExternalStorageDeviceModel>(props.model, "external-storage")}>
          {(model) => <StorageDetails model={model()} />}
        </Match>
        <Match when={asKind<KeyboardDeviceModel>(props.model, "keyboard")}>
          {(model) => <InputDetails model={model()} label={uiText.deviceSpecialized.keyboardInput} />}
        </Match>
        <Match when={asKind<MouseDeviceModel>(props.model, "mouse")}>
          {(model) => <InputDetails model={model()} label={uiText.deviceSpecialized.pointerInput} />}
        </Match>
        <Match when={asKind<AudioDeviceModel>(props.model, "audio-device")}>
          {(model) => <AudioDetails model={model()} />}
        </Match>
        <Match when={asKind<CameraDeviceModel>(props.model, "camera")}>
          {(model) => <CameraDetails model={model()} />}
        </Match>
        <Match when={asKind<MobileDeviceModel>(props.model, "mobile-device")}>
          {(model) => <MobileDetails model={model()} />}
        </Match>
        <Match when={asKind<PowerInputDeviceModel>(props.model, "power-input")}>
          {(model) => <PowerInputDetails model={model()} />}
        </Match>
        <Match when={asKind<GraphicsAdapterDeviceModel>(props.model, "graphics-adapter")}>
          {(model) => <GraphicsAdapterDetails model={model()} />}
        </Match>
        <Match when={asKind<NetworkAdapterDeviceModel>(props.model, "network-adapter")}>
          {(model) => <NetworkAdapterDetails model={model()} />}
        </Match>
        <Match when={asKind<BluetoothDeviceModel>(props.model, "bluetooth-device")}>
          {(model) => <BluetoothDetails model={model()} />}
        </Match>
        <Match when={asKind<InternalControllerDeviceModel>(props.model, "internal-controller")}>
          {(model) => <InternalControllerDetails model={model()} />}
        </Match>
        <Match when={asKind<GenericDeviceModel>(props.model, "generic-device")}>
          {(model) => <GenericDetails model={model()} />}
        </Match>
      </Switch>
      <CapabilityList values={props.model.capabilityLabels} />
    </section>
  );
}

function MonitorDetails(props: { model: MonitorDeviceModel }) {
  return (
    <>
      <div class="device-monitor-mode">
        <div>
          <span>{uiText.deviceSpecialized.activeResolution}</span>
          <strong>{props.model.resolution}</strong>
        </div>
        <div>
          <span>{uiText.deviceSpecialized.refreshRate}</span>
          <strong>{props.model.refreshRate}</strong>
        </div>
        <div>
          <span>{uiText.deviceSpecialized.hdrAdvancedColor}</span>
          <strong>{props.model.hdrState}</strong>
        </div>
        <div>
          <span>{uiText.deviceSpecialized.outputBitDepth}</span>
          <strong>{props.model.bitDepth}</strong>
        </div>
      </div>
      <FactRows fields={[
        [uiText.deviceSpecialized.displayTechnology, props.model.displayTechnology],
        [uiText.deviceSpecialized.panelTechnology, props.model.panelTechnology],
        [uiText.deviceSpecialized.colorEncoding, props.model.colorEncoding],
        [uiText.deviceSpecialized.colorSpace, props.model.colorSpace],
        [uiText.deviceSpecialized.sdrWhiteLevel, props.model.sdrWhiteLevel],
        [uiText.deviceSpecialized.peakLuminance, props.model.peakLuminance],
        [uiText.deviceSpecialized.fullFrameLuminance, props.model.fullFrameLuminance],
        [uiText.deviceSpecialized.physicalSize, props.model.physicalSize],
        [uiText.deviceSpecialized.imageConnector, props.model.connectorTechnology]
      ]} />
    </>
  );
}

function ExternalGpuDetails(props: { model: ExternalGpuDockDeviceModel }) {
  return (
    <FactRows fields={[
      [uiText.deviceSpecialized.interconnectTechnology, props.model.interconnectTechnology],
      [uiText.deviceSpecialized.graphicsDevice, props.model.gpuIdentity]
    ]} />
  );
}

function HubDetails(props: { model: DockDeviceModel | UsbHubDeviceModel }) {
  return (
    <>
      <div class="device-hub-stats">
        <Stat value={props.model.downstreamInterfaceCount} label={uiText.deviceSpecialized.downstreamInterfaces} />
        <Stat value={props.model.connectedDownstreamInterfaceCount} label={uiText.deviceSpecialized.connected} />
        <Stat value={props.model.idleDownstreamInterfaceCount} label={uiText.deviceSpecialized.idle} />
      </div>
      <FactRows fields={[
        [uiText.deviceSpecialized.currentLink, props.model.currentLink],
        [uiText.deviceSpecialized.deviceSpecification, props.model.usbSpecification]
      ]} />
    </>
  );
}

function StorageDetails(props: { model: ExternalStorageDeviceModel }) {
  return (
    <>
      <div class="device-storage-overview">
        <div>
          <span>{uiText.deviceSpecialized.driveCapacity}</span>
          <strong>{props.model.capacity}</strong>
        </div>
        <div>
          <span>{uiText.deviceSpecialized.healthState}</span>
          <strong>{props.model.healthStatus}</strong>
        </div>
      </div>
      <FactRows fields={reportedFacts([
        [uiText.deviceSpecialized.busType, props.model.busType],
        [uiText.deviceSpecialized.mediaType, props.model.mediaType],
        [uiText.deviceSpecialized.partitionStyle, props.model.partitionStyle],
        [uiText.deviceSpecialized.storageTransport, props.model.transportMode],
        [uiText.deviceSpecialized.currentLink, props.model.currentLink],
        [uiText.deviceSpecialized.usbSpecification, props.model.usbSpecification],
        [uiText.deviceSpecialized.deviceRevision, props.model.deviceRevision]
      ])} />
      <Show when={props.model.partitions.length > 0}>
        <div class="device-storage-partitions" aria-label={uiText.deviceSpecialized.partitionsAndVolumes}>
          <For each={props.model.partitions}>
            {(partition) => (
              <div class="device-storage-partition">
                <header>
                  <strong>{uiText.deviceSpecialized.partition(String(partition.partitionNumber ?? "--"))}</strong>
                  <span>{formatBytes(partition.capacityBytes)} · {partition.type ?? uiText.deviceSpecialized.formatNotReported}</span>
                </header>
                <Show when={partition.volumes.length > 0} fallback={<small>{uiText.deviceSpecialized.noMountedVolume}</small>}>
                  <For each={partition.volumes}>
                    {(volume) => (
                      <div class="device-storage-volume">
                        <div>
                          <strong>{volume.driveLetter ?? volume.label ?? uiText.deviceSpecialized.volumeWithoutLetter}</strong>
                          <span>{volume.fileSystem ?? uiText.deviceSpecialized.fileSystemNotReported} · {formatVolumeMountState(volume.mountState)}</span>
                        </div>
                        <small>{formatVolumeCapacity(volume.capacityBytes, volume.freeBytes)}</small>
                        <VolumeUsage capacity={volume.capacityBytes} free={volume.freeBytes} />
                      </div>
                    )}
                  </For>
                </Show>
              </div>
            )}
          </For>
        </div>
      </Show>
    </>
  );
}

function InputDetails(props: { model: KeyboardDeviceModel | MouseDeviceModel; label: string }) {
  const vendorMetric = () => props.model.kind === "keyboard"
    ? [uiText.deviceSpecialized.internalScanRate, props.model.scanRate] as const
    : ["DPI", props.model.dpi] as const;
  return (
    <>
      <div class="device-input-profile">
        <div>
          <span>{props.label}</span>
          <strong>{props.model.hidMode}</strong>
        </div>
        <Show when={props.model.deviceRevision !== "--"}>
          <small>{uiText.deviceSpecialized.deviceRevisionInline(props.model.deviceRevision)}</small>
        </Show>
      </div>
      <Show when={hasInputMetrics(props.model, vendorMetric()[1])}>
        <div class="device-input-metrics">
          <Metric label={uiText.deviceSpecialized.usbPollingInterval} value={props.model.pollingInterval} />
          <Metric label={uiText.deviceSpecialized.theoreticalReportRate} value={props.model.reportRate} />
          <Metric label={vendorMetric()[0]} value={vendorMetric()[1]} />
        </div>
      </Show>
    </>
  );
}

function AudioDetails(props: { model: AudioDeviceModel }) {
  return (
    <Show when={props.model.interfaceProtocols.length > 0}>
      <StatusStrip values={[
        [uiText.deviceSpecialized.audioControl, props.model.audioControl],
        [uiText.deviceSpecialized.audioStreaming, props.model.audioStreaming],
        [uiText.deviceSpecialized.hidControls, props.model.hidControls]
      ]} />
    </Show>
  );
}

function CameraDetails(props: { model: CameraDeviceModel }) {
  return (
    <>
      <Show when={props.model.bestMode !== "--"}>
        <div class="device-camera-best-mode">
          <span>{uiText.deviceSpecialized.highestNativeMode}</span>
          <strong>{props.model.bestMode}</strong>
        </div>
      </Show>
      <Show when={props.model.nativeModes.length > 0}>
        <div class="device-camera-modes" aria-label={uiText.deviceSpecialized.cameraNativeModes}>
          <For each={props.model.nativeModes}>
            {(mode) => (
              <span>{mode.width} x {mode.height} @ {mode.maximumFrameRate.toLocaleString(undefined, { maximumFractionDigits: 2 })} fps · {mode.pixelFormat}</span>
            )}
          </For>
        </div>
      </Show>
      <Show when={props.model.interfaceProtocols.length > 0}>
        <StatusStrip values={[
          [uiText.deviceSpecialized.videoControl, props.model.videoControl],
          [uiText.deviceSpecialized.videoStreaming, props.model.videoStreaming],
          [uiText.deviceSpecialized.companionAudio, props.model.audioCapable]
        ]} />
      </Show>
    </>
  );
}

function MobileDetails(props: { model: MobileDeviceModel }) {
  return (
    <>
      <FactRows fields={[
        [uiText.deviceSpecialized.manufacturer, props.model.manufacturer],
        [uiText.deviceSpecialized.model, props.model.model],
        [uiText.deviceSpecialized.deviceProtocol, props.model.protocol],
        [uiText.deviceSpecialized.connectionTransport, props.model.transport],
        [uiText.deviceSpecialized.firmwareVersion, props.model.firmwareVersion],
        [uiText.deviceSpecialized.battery, props.model.battery]
      ]} />
      <StatusStrip values={[
        [uiText.deviceSpecialized.mediaTransfer, props.model.mediaTransfer],
        [uiText.deviceSpecialized.dataConnection, props.model.dataConnection],
        [uiText.deviceSpecialized.vendorChannel, props.model.vendorChannel]
      ]} />
      <Show when={props.model.storages.length > 0}>
        <div class="device-smart-storage">
          <For each={props.model.storages}>
            {(storage) => (
              <div>
                <strong>{storage.name}</strong>
                <span>{storage.fileSystem ?? uiText.deviceSpecialized.fileSystemNotReported} · {formatVolumeCapacity(storage.capacityBytes, storage.freeBytes)}</span>
              </div>
            )}
          </For>
        </div>
      </Show>
    </>
  );
}

function PowerInputDetails(props: { model: PowerInputDeviceModel }) {
  return <FactRows fields={[
    [uiText.deviceSpecialized.powerRole, props.model.inputRole],
    [uiText.deviceSpecialized.negotiatedPower, props.model.negotiatedPower]
  ]} />;
}

function GraphicsAdapterDetails(props: { model: GraphicsAdapterDeviceModel }) {
  return <FactRows fields={reportedFacts([
    [uiText.deviceSpecialized.graphicsBus, props.model.busType]
  ])} />;
}

function NetworkAdapterDetails(props: { model: NetworkAdapterDeviceModel }) {
  return <FactRows fields={reportedFacts([
    [uiText.deviceSpecialized.networkInterface, props.model.interfaceName],
    [uiText.deviceSpecialized.connectionState, props.model.connectionState],
    [uiText.deviceSpecialized.transmitLink, props.model.transmitSpeed],
    [uiText.deviceSpecialized.receiveLink, props.model.receiveSpeed],
    [uiText.deviceSpecialized.activeMtu, props.model.activeMtu],
    [uiText.deviceSpecialized.macAddress, props.model.permanentAddress]
  ])} />;
}

function BluetoothDetails(props: { model: BluetoothDeviceModel }) {
  return <FactRows fields={reportedFacts([
    [uiText.deviceSpecialized.deviceRole, props.model.bluetoothRole],
    [uiText.deviceSpecialized.transportProtocol, props.model.protocol]
  ])} />;
}

function InternalControllerDetails(props: { model: InternalControllerDeviceModel }) {
  return <FactRows fields={reportedFacts([
    [uiText.deviceSpecialized.controllerType, props.model.controllerType],
    [uiText.deviceSpecialized.interconnectTechnology, props.model.interconnectTechnology],
    [uiText.deviceSpecialized.downstreamDevices, String(props.model.downstreamDeviceCount)]
  ])} />;
}

function GenericDetails(props: { model: GenericDeviceModel }) {
  return <FactRows fields={reportedFacts([
    [uiText.deviceSpecialized.deviceClass, props.model.deviceClass],
    [uiText.deviceSpecialized.usbSpecification, props.model.usbSpecification],
    [uiText.deviceSpecialized.deviceRevision, props.model.deviceRevision]
  ])} />;
}

function hasInputMetrics(model: KeyboardDeviceModel | MouseDeviceModel, vendorMetric: string) {
  return model.pollingInterval !== "--"
    || model.reportRate !== "--"
    || (vendorMetric !== "--" && !vendorMetric.includes(uiText.deviceSpecialized.notReported));
}

function FactRows(props: { fields: Array<[string, string]> }) {
  return (
    <div class="device-specialized-facts">
      <For each={props.fields}>
        {([label, value]) => <><span>{label}</span><strong>{value}</strong></>}
      </For>
    </div>
  );
}

function reportedFacts(fields: Array<[string, string]>) {
  return fields.filter(([, value]) => value !== "--");
}

function StatusStrip(props: { values: Array<[string, boolean]> }) {
  return (
    <div class="device-capability-status">
      <For each={props.values}>
        {([label, active]) => (
          <div classList={{ active }}>
            <span>{label}</span>
            <strong>{active ? uiText.deviceSpecialized.supported : uiText.deviceSpecialized.notReported}</strong>
          </div>
        )}
      </For>
    </div>
  );
}

function CapabilityList(props: { values: string[] }) {
  return (
    <Show when={props.values.length > 0}>
      <div class="device-capability-list" aria-label={uiText.deviceSpecialized.deviceCapabilities}>
        <For each={props.values}>{(value) => <span>{value}</span>}</For>
      </div>
    </Show>
  );
}

function Stat(props: { value: number; label: string }) {
  return <div><strong>{props.value}</strong><span>{props.label}</span></div>;
}

function Metric(props: { label: string; value: string }) {
  return <div><span>{props.label}</span><strong>{props.value}</strong></div>;
}

function VolumeUsage(props: { capacity?: number | null; free?: number | null }) {
  const usedPercent = () => {
    if (!props.capacity || typeof props.free !== "number") return 0;
    return Math.max(0, Math.min(100, ((props.capacity - props.free) / props.capacity) * 100));
  };
  return <div class="device-storage-usage" aria-label={uiText.deviceSpecialized.usedPercent(usedPercent().toFixed(1))}><span style={{ width: `${usedPercent()}%` }} /></div>;
}

function formatVolumeCapacity(capacity?: number | null, free?: number | null) {
  if (typeof capacity !== "number") return uiText.deviceSpecialized.capacityNotReported;
  return typeof free === "number"
    ? uiText.deviceSpecialized.usedOfTotal(formatBytes(capacity - free), formatBytes(capacity))
    : formatBytes(capacity);
}

function formatVolumeMountState(value: string) {
  return mountStateLabel(value.trim()) ?? uiText.deviceSpecialized.volumeStateUnknown;
}

function asKind<T extends SpecializedDeviceModel>(model: SpecializedDeviceModel, kind: T["kind"]) {
  return model.kind === kind ? model as T : undefined;
}

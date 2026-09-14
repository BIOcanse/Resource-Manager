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
} from "../../deviceTopology/adapters/types";
import { formatBytes } from "../../deviceTopology/adapters/adapterEvidence";
import "./specialized-device-details.css";

export function DeviceSpecializedDetails(props: { model: SpecializedDeviceModel }) {
  return (
    <section class={`device-specialized-details kind-${props.model.kind}`} aria-label={`${props.model.deviceTypeLabel}专属信息`}>
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
          {(model) => <InputDetails model={model()} label="键盘输入" />}
        </Match>
        <Match when={asKind<MouseDeviceModel>(props.model, "mouse")}>
          {(model) => <InputDetails model={model()} label="指针输入" />}
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
          <span>活动分辨率</span>
          <strong>{props.model.resolution}</strong>
        </div>
        <div>
          <span>刷新率</span>
          <strong>{props.model.refreshRate}</strong>
        </div>
        <div>
          <span>HDR / 高级颜色</span>
          <strong>{props.model.hdrState}</strong>
        </div>
        <div>
          <span>输出位深</span>
          <strong>{props.model.bitDepth}</strong>
        </div>
      </div>
      <FactRows fields={[
        ["显示技术", props.model.displayTechnology],
        ["面板类型", props.model.panelTechnology],
        ["颜色编码", props.model.colorEncoding],
        ["色彩空间", props.model.colorSpace],
        ["SDR 白电平", props.model.sdrWhiteLevel],
        ["峰值亮度", props.model.peakLuminance],
        ["全屏亮度", props.model.fullFrameLuminance],
        ["物理尺寸", props.model.physicalSize],
        ["图像接口", props.model.connectorTechnology]
      ]} />
    </>
  );
}

function ExternalGpuDetails(props: { model: ExternalGpuDockDeviceModel }) {
  return (
    <FactRows fields={[
      ["互连技术", props.model.interconnectTechnology],
      ["图形设备", props.model.gpuIdentity]
    ]} />
  );
}

function HubDetails(props: { model: DockDeviceModel | UsbHubDeviceModel }) {
  return (
    <>
      <div class="device-hub-stats">
        <Stat value={props.model.downstreamInterfaceCount} label="下游接口" />
        <Stat value={props.model.connectedDownstreamInterfaceCount} label="已连接" />
        <Stat value={props.model.idleDownstreamInterfaceCount} label="空闲" />
      </div>
      <FactRows fields={[
        ["当前链路", props.model.currentLink],
        ["设备规范", props.model.usbSpecification]
      ]} />
    </>
  );
}

function StorageDetails(props: { model: ExternalStorageDeviceModel }) {
  return (
    <>
      <div class="device-storage-overview">
        <div>
          <span>整盘容量</span>
          <strong>{props.model.capacity}</strong>
        </div>
        <div>
          <span>健康状态</span>
          <strong>{props.model.healthStatus}</strong>
        </div>
      </div>
      <FactRows fields={reportedFacts([
        ["总线类型", props.model.busType],
        ["介质类型", props.model.mediaType],
        ["分区样式", props.model.partitionStyle],
        ["存储传输", props.model.transportMode],
        ["当前链路", props.model.currentLink],
        ["USB 规范", props.model.usbSpecification],
        ["设备版本", props.model.deviceRevision]
      ])} />
      <Show when={props.model.partitions.length > 0}>
        <div class="device-storage-partitions" aria-label="磁盘分区和卷">
          <For each={props.model.partitions}>
            {(partition) => (
              <div class="device-storage-partition">
                <header>
                  <strong>分区 {partition.partitionNumber ?? "--"}</strong>
                  <span>{formatBytes(partition.capacityBytes)} · {partition.type ?? "格式未报告"}</span>
                </header>
                <Show when={partition.volumes.length > 0} fallback={<small>未挂载卷</small>}>
                  <For each={partition.volumes}>
                    {(volume) => (
                      <div class="device-storage-volume">
                        <div>
                          <strong>{volume.driveLetter ?? volume.label ?? "无盘符卷"}</strong>
                          <span>{volume.fileSystem ?? "文件系统未报告"} · {formatVolumeMountState(volume.mountState)}</span>
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
    ? ["内部扫描率", props.model.scanRate] as const
    : ["DPI", props.model.dpi] as const;
  return (
    <>
      <div class="device-input-profile">
        <div>
          <span>{props.label}</span>
          <strong>{props.model.hidMode}</strong>
        </div>
        <Show when={props.model.deviceRevision !== "--"}>
          <small>设备版本 {props.model.deviceRevision}</small>
        </Show>
      </div>
      <Show when={hasInputMetrics(props.model, vendorMetric()[1])}>
        <div class="device-input-metrics">
          <Metric label="USB 轮询周期" value={props.model.pollingInterval} />
          <Metric label="理论报告率" value={props.model.reportRate} />
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
        ["音频控制", props.model.audioControl],
        ["音频流", props.model.audioStreaming],
        ["HID 控制", props.model.hidControls]
      ]} />
    </Show>
  );
}

function CameraDetails(props: { model: CameraDeviceModel }) {
  return (
    <>
      <Show when={props.model.bestMode !== "--"}>
        <div class="device-camera-best-mode">
          <span>最高原生模式</span>
          <strong>{props.model.bestMode}</strong>
        </div>
      </Show>
      <Show when={props.model.nativeModes.length > 0}>
        <div class="device-camera-modes" aria-label="摄像头原生模式">
          <For each={props.model.nativeModes}>
            {(mode) => (
              <span>{mode.width} x {mode.height} @ {mode.maximumFrameRate.toLocaleString(undefined, { maximumFractionDigits: 2 })} fps · {mode.pixelFormat}</span>
            )}
          </For>
        </div>
      </Show>
      <Show when={props.model.interfaceProtocols.length > 0}>
        <StatusStrip values={[
          ["视频控制", props.model.videoControl],
          ["视频流", props.model.videoStreaming],
          ["伴随音频", props.model.audioCapable]
        ]} />
      </Show>
    </>
  );
}

function MobileDetails(props: { model: MobileDeviceModel }) {
  return (
    <>
      <FactRows fields={[
        ["制造商", props.model.manufacturer],
        ["型号", props.model.model],
        ["设备协议", props.model.protocol],
        ["连接传输", props.model.transport],
        ["固件版本", props.model.firmwareVersion],
        ["电量", props.model.battery]
      ]} />
      <StatusStrip values={[
        ["媒体传输", props.model.mediaTransfer],
        ["数据连接", props.model.dataConnection],
        ["厂商通道", props.model.vendorChannel]
      ]} />
      <Show when={props.model.storages.length > 0}>
        <div class="device-smart-storage">
          <For each={props.model.storages}>
            {(storage) => (
              <div>
                <strong>{storage.name}</strong>
                <span>{storage.fileSystem ?? "文件系统未报告"} · {formatVolumeCapacity(storage.capacityBytes, storage.freeBytes)}</span>
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
    ["供电角色", props.model.inputRole],
    ["协商功率", props.model.negotiatedPower]
  ]} />;
}

function GraphicsAdapterDetails(props: { model: GraphicsAdapterDeviceModel }) {
  return <FactRows fields={reportedFacts([
    ["图形总线", props.model.busType]
  ])} />;
}

function NetworkAdapterDetails(props: { model: NetworkAdapterDeviceModel }) {
  return <FactRows fields={reportedFacts([
    ["网络接口", props.model.interfaceName],
    ["连接状态", props.model.connectionState],
    ["发送链路", props.model.transmitSpeed],
    ["接收链路", props.model.receiveSpeed],
    ["活动 MTU", props.model.activeMtu],
    ["MAC 地址", props.model.permanentAddress]
  ])} />;
}

function BluetoothDetails(props: { model: BluetoothDeviceModel }) {
  return <FactRows fields={reportedFacts([
    ["设备角色", props.model.bluetoothRole],
    ["传输协议", props.model.protocol]
  ])} />;
}

function InternalControllerDetails(props: { model: InternalControllerDeviceModel }) {
  return <FactRows fields={reportedFacts([
    ["控制器类型", props.model.controllerType],
    ["互连技术", props.model.interconnectTechnology],
    ["下游设备", String(props.model.downstreamDeviceCount)]
  ])} />;
}

function GenericDetails(props: { model: GenericDeviceModel }) {
  return <FactRows fields={reportedFacts([
    ["设备类", props.model.deviceClass],
    ["USB 规范", props.model.usbSpecification],
    ["设备版本", props.model.deviceRevision]
  ])} />;
}

function hasInputMetrics(model: KeyboardDeviceModel | MouseDeviceModel, vendorMetric: string) {
  return model.pollingInterval !== "--"
    || model.reportRate !== "--"
    || (vendorMetric !== "--" && !vendorMetric.includes("未报告"));
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
            <strong>{active ? "支持" : "未报告"}</strong>
          </div>
        )}
      </For>
    </div>
  );
}

function CapabilityList(props: { values: string[] }) {
  return (
    <Show when={props.values.length > 0}>
      <div class="device-capability-list" aria-label="设备能力">
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
  return <div class="device-storage-usage" aria-label={`已用 ${usedPercent().toFixed(1)}%`}><span style={{ width: `${usedPercent()}%` }} /></div>;
}

function formatVolumeCapacity(capacity?: number | null, free?: number | null) {
  if (typeof capacity !== "number") return "容量未报告";
  return typeof free === "number"
    ? `${formatBytes(capacity - free)} 已用 / ${formatBytes(capacity)}`
    : formatBytes(capacity);
}

function formatVolumeMountState(value: string) {
  switch (value.trim().toLocaleLowerCase()) {
    case "mounted":
    case "online":
    case "available": return "可用";
    case "unmounted":
    case "offline": return "未挂载";
    default: return value || "状态未知";
  }
}

function asKind<T extends SpecializedDeviceModel>(model: SpecializedDeviceModel, kind: T["kind"]) {
  return model.kind === kind ? model as T : undefined;
}

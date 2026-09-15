import {
  requireArray,
  requireBackendMessage,
  requireBoolean,
  requireNonEmptyString,
  requireNonNegativeFiniteNumber,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireRecord,
  requireString,
  requireStringArray
} from "../frontendRuntime/request/ResponseDecoder.ts";
import type {
  DeviceTopologyAdvancedInterconnect,
  DeviceTopologyCameraCapabilities,
  DeviceTopologyCameraMode,
  DeviceTopologyDisplayConnection,
  DeviceTopologyHidCapabilities,
  DeviceTopologyIdResolution,
  DeviceTopologyNetworkConnection,
  DeviceTopologyPort,
  DeviceTopologySmartDeviceCapabilities,
  DeviceTopologySmartDeviceStorage,
  DeviceTopologyStorageDevice,
  DeviceTopologyStoragePartition,
  DeviceTopologyStorageVolume,
  DeviceTopologySystemIdentity,
  DeviceTopologyUsbCompanionPort,
  DeviceTopologyUsbConnection,
  DeviceTopologyUsbEndpoint
} from "../types.ts";

export function decodeSystemIdentity(
  value: unknown,
  path: string
): DeviceTopologySystemIdentity {
  const record = requireRecord(value, path);
  return {
    manufacturer: requireString(record.manufacturer, `${path}.manufacturer`),
    model: requireString(record.model, `${path}.model`),
    brandDisplayName: requireString(record.brandDisplayName, `${path}.brandDisplayName`),
    brandLogoText: requireString(record.brandLogoText, `${path}.brandLogoText`),
    biosVersion: nullableString(record.biosVersion, `${path}.biosVersion`),
    baseBoardManufacturer: nullableString(
      record.baseBoardManufacturer,
      `${path}.baseBoardManufacturer`),
    baseBoardProduct: nullableString(record.baseBoardProduct, `${path}.baseBoardProduct`)
  };
}

export function decodePort(value: unknown, path: string): DeviceTopologyPort {
  const record = requireRecord(value, path);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    isPhysicalConnector: requireBoolean(record.isPhysicalConnector, `${path}.isPhysicalConnector`),
    displayName: requireString(record.displayName, `${path}.displayName`),
    connectorKind: requireString(record.connectorKind, `${path}.connectorKind`),
    busKind: requireString(record.busKind, `${path}.busKind`),
    hardwareKind: requireString(record.hardwareKind, `${path}.hardwareKind`),
    protocol: requireString(record.protocol, `${path}.protocol`),
    speed: requireString(record.speed, `${path}.speed`),
    physicalMaximumSpeed: nullableString(
      record.physicalMaximumSpeed,
      `${path}.physicalMaximumSpeed`),
    deviceId: requireNonEmptyString(record.deviceId, `${path}.deviceId`),
    pnpClass: nullableString(record.pnpClass, `${path}.pnpClass`),
    manufacturer: nullableString(record.manufacturer, `${path}.manufacturer`),
    service: nullableString(record.service, `${path}.service`),
    status: nullableString(record.status, `${path}.status`),
    confidence: requireBackendMessage(record.confidence, `${path}.confidence`),
    source: requireBackendMessage(record.source, `${path}.source`),
    upstreamDeviceId: nullableString(record.upstreamDeviceId, `${path}.upstreamDeviceId`),
    upstreamDisplayName: nullableString(
      record.upstreamDisplayName,
      `${path}.upstreamDisplayName`),
    topologyPath: requireString(record.topologyPath, `${path}.topologyPath`),
    nativeParentDeviceId: nullableString(
      record.nativeParentDeviceId,
      `${path}.nativeParentDeviceId`),
    nativeParentDisplayName: nullableString(
      record.nativeParentDisplayName,
      `${path}.nativeParentDisplayName`),
    locationInfo: nullableString(record.locationInfo, `${path}.locationInfo`),
    locationPaths: requireStringArray(record.locationPaths, `${path}.locationPaths`),
    classGuid: nullableString(record.classGuid, `${path}.classGuid`),
    display: nullableObject(record.display, `${path}.display`, decodeDisplayConnection),
    network: nullableObject(record.network, `${path}.network`, decodeNetworkConnection),
    idResolution: nullableObject(
      record.idResolution,
      `${path}.idResolution`,
      decodeIdResolution),
    advancedInterconnect: nullableObject(
      record.advancedInterconnect,
      `${path}.advancedInterconnect`,
      decodeAdvancedInterconnect),
    usb: nullableObject(record.usb, `${path}.usb`, decodeUsbConnection),
    hardwareIds: requireStringArray(record.hardwareIds, `${path}.hardwareIds`),
    compatibleIds: requireStringArray(record.compatibleIds, `${path}.compatibleIds`),
    devNodeStatus: nullableInteger(record.devNodeStatus, `${path}.devNodeStatus`),
    problemCode: nullableInteger(record.problemCode, `${path}.problemCode`),
    hid: nullableObject(record.hid, `${path}.hid`, decodeHidCapabilities),
    camera: nullableObject(record.camera, `${path}.camera`, decodeCameraCapabilities),
    smartDevice: nullableObject(
      record.smartDevice,
      `${path}.smartDevice`,
      decodeSmartDeviceCapabilities),
    storage: nullableObject(record.storage, `${path}.storage`, decodeStorageDevice)
  };
}

function decodeDisplayConnection(
  value: unknown,
  path: string
): DeviceTopologyDisplayConnection {
  const record = requireRecord(value, path);
  return {
    connectorTechnology: requireString(
      record.connectorTechnology,
      `${path}.connectorTechnology`),
    monitorName: requireString(record.monitorName, `${path}.monitorName`),
    resolution: nullableString(record.resolution, `${path}.resolution`),
    refreshRate: requireString(record.refreshRate, `${path}.refreshRate`),
    active: requireBoolean(record.active, `${path}.active`),
    targetAvailable: requireBoolean(record.targetAvailable, `${path}.targetAvailable`),
    internal: requireBoolean(record.internal, `${path}.internal`),
    connectorInstance: requireNonNegativeSafeInteger(
      record.connectorInstance,
      `${path}.connectorInstance`),
    monitorDevicePath: nullableString(
      record.monitorDevicePath,
      `${path}.monitorDevicePath`),
    bitsPerColorChannel: nullableInteger(
      record.bitsPerColorChannel,
      `${path}.bitsPerColorChannel`),
    colorEncoding: nullableString(record.colorEncoding, `${path}.colorEncoding`),
    advancedColorSupported: nullableBoolean(
      record.advancedColorSupported,
      `${path}.advancedColorSupported`),
    advancedColorEnabled: nullableBoolean(
      record.advancedColorEnabled,
      `${path}.advancedColorEnabled`),
    wideColorEnforced: nullableBoolean(
      record.wideColorEnforced,
      `${path}.wideColorEnforced`),
    sdrWhiteLevelNits: nullableNumber(
      record.sdrWhiteLevelNits,
      `${path}.sdrWhiteLevelNits`),
    hdrFormats: nullableString(record.hdrFormats, `${path}.hdrFormats`),
    displayTechnology: nullableString(
      record.displayTechnology,
      `${path}.displayTechnology`),
    panelTechnology: nullableString(record.panelTechnology, `${path}.panelTechnology`),
    edidVersion: nullableString(record.edidVersion, `${path}.edidVersion`),
    edidProductName: nullableString(record.edidProductName, `${path}.edidProductName`),
    edidSerialNumber: nullableString(record.edidSerialNumber, `${path}.edidSerialNumber`),
    physicalSize: nullableString(record.physicalSize, `${path}.physicalSize`),
    minimumLuminanceNits: nullableNumber(
      record.minimumLuminanceNits,
      `${path}.minimumLuminanceNits`),
    maximumLuminanceNits: nullableNumber(
      record.maximumLuminanceNits,
      `${path}.maximumLuminanceNits`),
    maximumFullFrameLuminanceNits: nullableNumber(
      record.maximumFullFrameLuminanceNits,
      `${path}.maximumFullFrameLuminanceNits`),
    colorCapabilitySource: nullableString(
      record.colorCapabilitySource,
      `${path}.colorCapabilitySource`),
    colorSpace: nullableString(record.colorSpace, `${path}.colorSpace`)
  };
}

function decodeNetworkConnection(
  value: unknown,
  path: string
): DeviceTopologyNetworkConnection {
  const record = requireRecord(value, path);
  return {
    interfaceName: nullableString(record.interfaceName, `${path}.interfaceName`),
    connectionState: requireString(record.connectionState, `${path}.connectionState`),
    transmitLinkSpeed: nullableString(
      record.transmitLinkSpeed,
      `${path}.transmitLinkSpeed`),
    receiveLinkSpeed: nullableString(record.receiveLinkSpeed, `${path}.receiveLinkSpeed`),
    permanentAddress: nullableString(record.permanentAddress, `${path}.permanentAddress`),
    activeMtuBytes: nullableInteger(record.activeMtuBytes, `${path}.activeMtuBytes`),
    hardwareInterface: nullableBoolean(
      record.hardwareInterface,
      `${path}.hardwareInterface`),
    connectorPresent: nullableBoolean(record.connectorPresent, `${path}.connectorPresent`)
  };
}

function decodeIdResolution(value: unknown, path: string): DeviceTopologyIdResolution {
  const record = requireRecord(value, path);
  return {
    database: requireString(record.database, `${path}.database`),
    version: requireString(record.version, `${path}.version`),
    vendorName: nullableString(record.vendorName, `${path}.vendorName`),
    deviceName: nullableString(record.deviceName, `${path}.deviceName`),
    subsystemName: nullableString(record.subsystemName, `${path}.subsystemName`)
  };
}

function decodeAdvancedInterconnect(
  value: unknown,
  path: string
): DeviceTopologyAdvancedInterconnect {
  const record = requireRecord(value, path);
  return {
    kind: requireString(record.kind, `${path}.kind`),
    role: requireBackendMessage(record.role, `${path}.role`),
    technology: requireString(record.technology, `${path}.technology`),
    evidence: requireBackendMessage(record.evidence, `${path}.evidence`)
  };
}

function decodeUsbConnection(value: unknown, path: string): DeviceTopologyUsbConnection {
  const record = requireRecord(value, path);
  return {
    hubDevicePath: requireNonEmptyString(record.hubDevicePath, `${path}.hubDevicePath`),
    portNumber: requireNonNegativeSafeInteger(record.portNumber, `${path}.portNumber`),
    deviceConnected: requireBoolean(record.deviceConnected, `${path}.deviceConnected`),
    connectionStatus: requireString(record.connectionStatus, `${path}.connectionStatus`),
    negotiatedSpeed: requireString(record.negotiatedSpeed, `${path}.negotiatedSpeed`),
    deviceAddress: requireNonNegativeSafeInteger(record.deviceAddress, `${path}.deviceAddress`),
    vendorId: nullableString(record.vendorId, `${path}.vendorId`),
    productId: nullableString(record.productId, `${path}.productId`),
    deviceIsHub: requireBoolean(record.deviceIsHub, `${path}.deviceIsHub`),
    supportedProtocols: requireString(record.supportedProtocols, `${path}.supportedProtocols`),
    operatingAtSuperSpeedOrHigher: nullableBoolean(
      record.operatingAtSuperSpeedOrHigher,
      `${path}.operatingAtSuperSpeedOrHigher`),
    superSpeedCapableOrHigher: nullableBoolean(
      record.superSpeedCapableOrHigher,
      `${path}.superSpeedCapableOrHigher`),
    operatingAtSuperSpeedPlusOrHigher: nullableBoolean(
      record.operatingAtSuperSpeedPlusOrHigher,
      `${path}.operatingAtSuperSpeedPlusOrHigher`),
    superSpeedPlusCapableOrHigher: nullableBoolean(
      record.superSpeedPlusCapableOrHigher,
      `${path}.superSpeedPlusCapableOrHigher`),
    portIsUserConnectable: nullableBoolean(
      record.portIsUserConnectable,
      `${path}.portIsUserConnectable`),
    portIsDebugCapable: nullableBoolean(
      record.portIsDebugCapable,
      `${path}.portIsDebugCapable`),
    portHasMultipleCompanions: nullableBoolean(
      record.portHasMultipleCompanions,
      `${path}.portHasMultipleCompanions`),
    portConnectorIsTypeC: nullableBoolean(
      record.portConnectorIsTypeC,
      `${path}.portConnectorIsTypeC`),
    companionPorts: requireArray(record.companionPorts, `${path}.companionPorts`)
      .map((item, index) => decodeUsbCompanionPort(item, `${path}.companionPorts[${index}]`)),
    deviceSpecification: requireString(
      record.deviceSpecification,
      `${path}.deviceSpecification`),
    deviceRevision: requireString(record.deviceRevision, `${path}.deviceRevision`),
    deviceClass: requireString(record.deviceClass, `${path}.deviceClass`),
    manufacturerName: nullableString(record.manufacturerName, `${path}.manufacturerName`),
    productName: nullableString(record.productName, `${path}.productName`),
    serialNumber: nullableString(record.serialNumber, `${path}.serialNumber`),
    interfaceProtocols: requireStringArray(
      record.interfaceProtocols,
      `${path}.interfaceProtocols`),
    downstreamHubDevicePath: nullableString(
      record.downstreamHubDevicePath,
      `${path}.downstreamHubDevicePath`),
    endpoints: requireNullable(
      record.endpoints,
      `${path}.endpoints`,
      (items, itemPath) => requireArray(items, itemPath)
        .map((item, index) => decodeUsbEndpoint(item, `${itemPath}[${index}]`)))
  };
}

function decodeUsbCompanionPort(
  value: unknown,
  path: string
): DeviceTopologyUsbCompanionPort {
  const record = requireRecord(value, path);
  return {
    companionIndex: requireNonNegativeSafeInteger(
      record.companionIndex,
      `${path}.companionIndex`),
    portNumber: requireNonNegativeSafeInteger(record.portNumber, `${path}.portNumber`),
    hubSymbolicLinkName: nullableString(
      record.hubSymbolicLinkName,
      `${path}.hubSymbolicLinkName`)
  };
}

function decodeUsbEndpoint(value: unknown, path: string): DeviceTopologyUsbEndpoint {
  const record = requireRecord(value, path);
  return {
    interfaceNumber: requireNonNegativeSafeInteger(
      record.interfaceNumber,
      `${path}.interfaceNumber`),
    alternateSetting: requireNonNegativeSafeInteger(
      record.alternateSetting,
      `${path}.alternateSetting`),
    interfaceProtocol: requireString(record.interfaceProtocol, `${path}.interfaceProtocol`),
    endpointAddress: requireNonNegativeSafeInteger(
      record.endpointAddress,
      `${path}.endpointAddress`),
    direction: requireString(record.direction, `${path}.direction`),
    transferType: requireString(record.transferType, `${path}.transferType`),
    maximumPacketSize: requireNonNegativeSafeInteger(
      record.maximumPacketSize,
      `${path}.maximumPacketSize`),
    interval: requireNonNegativeSafeInteger(record.interval, `${path}.interval`),
    serviceIntervalMicroseconds: nullableNumber(
      record.serviceIntervalMicroseconds,
      `${path}.serviceIntervalMicroseconds`),
    theoreticalReportRateHz: nullableNumber(
      record.theoreticalReportRateHz,
      `${path}.theoreticalReportRateHz`)
  };
}

function decodeHidCapabilities(
  value: unknown,
  path: string
): DeviceTopologyHidCapabilities {
  const record = requireRecord(value, path);
  return {
    hidType: requireString(record.hidType, `${path}.hidType`),
    hidSpecification: nullableString(record.hidSpecification, `${path}.hidSpecification`),
    inputPollingIntervalMicroseconds: nullableNumber(
      record.inputPollingIntervalMicroseconds,
      `${path}.inputPollingIntervalMicroseconds`),
    theoreticalReportRateHz: nullableNumber(
      record.theoreticalReportRateHz,
      `${path}.theoreticalReportRateHz`),
    reportedDpi: nullableInteger(record.reportedDpi, `${path}.reportedDpi`),
    reportedScanRateHz: nullableInteger(
      record.reportedScanRateHz,
      `${path}.reportedScanRateHz`),
    standardCapabilitySource: requireString(
      record.standardCapabilitySource,
      `${path}.standardCapabilitySource`),
    vendorCapabilitySource: nullableString(
      record.vendorCapabilitySource,
      `${path}.vendorCapabilitySource`)
  };
}

function decodeCameraCapabilities(
  value: unknown,
  path: string
): DeviceTopologyCameraCapabilities {
  const record = requireRecord(value, path);
  return {
    capabilitySource: requireString(record.capabilitySource, `${path}.capabilitySource`),
    bestMode: nullableObject(record.bestMode, `${path}.bestMode`, decodeCameraMode),
    nativeModes: requireArray(record.nativeModes, `${path}.nativeModes`)
      .map((item, index) => decodeCameraMode(item, `${path}.nativeModes[${index}]`))
  };
}

function decodeCameraMode(value: unknown, path: string): DeviceTopologyCameraMode {
  const record = requireRecord(value, path);
  return {
    width: requireNonNegativeSafeInteger(record.width, `${path}.width`),
    height: requireNonNegativeSafeInteger(record.height, `${path}.height`),
    maximumFrameRate: requireNonNegativeFiniteNumber(
      record.maximumFrameRate,
      `${path}.maximumFrameRate`),
    pixelFormat: requireString(record.pixelFormat, `${path}.pixelFormat`)
  };
}

function decodeSmartDeviceCapabilities(
  value: unknown,
  path: string
): DeviceTopologySmartDeviceCapabilities {
  const record = requireRecord(value, path);
  return {
    deviceType: requireString(record.deviceType, `${path}.deviceType`),
    manufacturer: nullableString(record.manufacturer, `${path}.manufacturer`),
    model: nullableString(record.model, `${path}.model`),
    serialNumber: nullableString(record.serialNumber, `${path}.serialNumber`),
    firmwareVersion: nullableString(record.firmwareVersion, `${path}.firmwareVersion`),
    protocol: nullableString(record.protocol, `${path}.protocol`),
    transport: nullableString(record.transport, `${path}.transport`),
    batteryPercent: nullableInteger(record.batteryPercent, `${path}.batteryPercent`),
    storages: requireArray(record.storages, `${path}.storages`)
      .map((item, index) => decodeSmartDeviceStorage(item, `${path}.storages[${index}]`)),
    source: requireString(record.source, `${path}.source`)
  };
}

function decodeSmartDeviceStorage(
  value: unknown,
  path: string
): DeviceTopologySmartDeviceStorage {
  const record = requireRecord(value, path);
  return {
    name: requireString(record.name, `${path}.name`),
    capacityBytes: nullableInteger(record.capacityBytes, `${path}.capacityBytes`),
    freeBytes: nullableInteger(record.freeBytes, `${path}.freeBytes`),
    fileSystem: nullableString(record.fileSystem, `${path}.fileSystem`)
  };
}

function decodeStorageDevice(value: unknown, path: string): DeviceTopologyStorageDevice {
  const record = requireRecord(value, path);
  return {
    physicalDeviceId: requireNonEmptyString(
      record.physicalDeviceId,
      `${path}.physicalDeviceId`),
    model: nullableString(record.model, `${path}.model`),
    manufacturer: nullableString(record.manufacturer, `${path}.manufacturer`),
    serialNumber: nullableString(record.serialNumber, `${path}.serialNumber`),
    firmwareRevision: nullableString(record.firmwareRevision, `${path}.firmwareRevision`),
    mediaType: nullableString(record.mediaType, `${path}.mediaType`),
    busType: nullableString(record.busType, `${path}.busType`),
    capacityBytes: nullableInteger(record.capacityBytes, `${path}.capacityBytes`),
    bytesPerSector: nullableInteger(record.bytesPerSector, `${path}.bytesPerSector`),
    partitionStyle: nullableString(record.partitionStyle, `${path}.partitionStyle`),
    healthStatus: nullableString(record.healthStatus, `${path}.healthStatus`),
    partitions: requireArray(record.partitions, `${path}.partitions`)
      .map((item, index) => decodeStoragePartition(item, `${path}.partitions[${index}]`)),
    source: requireString(record.source, `${path}.source`)
  };
}

function decodeStoragePartition(
  value: unknown,
  path: string
): DeviceTopologyStoragePartition {
  const record = requireRecord(value, path);
  return {
    deviceId: requireNonEmptyString(record.deviceId, `${path}.deviceId`),
    partitionNumber: nullableInteger(record.partitionNumber, `${path}.partitionNumber`),
    type: nullableString(record.type, `${path}.type`),
    capacityBytes: nullableInteger(record.capacityBytes, `${path}.capacityBytes`),
    startingOffsetBytes: nullableInteger(
      record.startingOffsetBytes,
      `${path}.startingOffsetBytes`),
    bootable: nullableBoolean(record.bootable, `${path}.bootable`),
    bootPartition: nullableBoolean(record.bootPartition, `${path}.bootPartition`),
    primaryPartition: nullableBoolean(record.primaryPartition, `${path}.primaryPartition`),
    volumes: requireArray(record.volumes, `${path}.volumes`)
      .map((item, index) => decodeStorageVolume(item, `${path}.volumes[${index}]`))
  };
}

function decodeStorageVolume(
  value: unknown,
  path: string
): DeviceTopologyStorageVolume {
  const record = requireRecord(value, path);
  return {
    driveLetter: nullableString(record.driveLetter, `${path}.driveLetter`),
    label: nullableString(record.label, `${path}.label`),
    fileSystem: nullableString(record.fileSystem, `${path}.fileSystem`),
    capacityBytes: nullableInteger(record.capacityBytes, `${path}.capacityBytes`),
    freeBytes: nullableInteger(record.freeBytes, `${path}.freeBytes`),
    mountState: requireString(record.mountState, `${path}.mountState`),
    volumeSerialNumber: nullableString(
      record.volumeSerialNumber,
      `${path}.volumeSerialNumber`)
  };
}

function nullableString(value: unknown, path: string): string | null {
  return requireNullable(value, path, requireString);
}

function nullableBoolean(value: unknown, path: string): boolean | null {
  return requireNullable(value, path, requireBoolean);
}

function nullableInteger(value: unknown, path: string): number | null {
  return requireNullable(value, path, requireNonNegativeSafeInteger);
}

function nullableNumber(value: unknown, path: string): number | null {
  return requireNullable(value, path, requireNonNegativeFiniteNumber);
}

function nullableObject<T>(
  value: unknown,
  path: string,
  decode: (value: unknown, path: string) => T
): T | null {
  return requireNullable(value, path, decode);
}

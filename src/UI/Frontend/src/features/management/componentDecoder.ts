import type { ManagedComponent, ComponentCapability, ComponentProviderState } from "../../types.ts";
import { requireArray, requireRecord, requireString, requireStringArray, requireBoolean,
  requireOneOf, requireNullable, requireBackendMessage, ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";

const states = ["active", "installed", "installed_unverified", "payload_missing", "update_available", "ready_to_install", "available", "manual_download_required", "provider_missing", "missing"] as const;
const providerStates = ["active", "installed", "missing", "runtime_available", "installed_unverified", "driver_missing", "runtime_active"] as const;
const providerKinds = ["runtime", "cpu", "fan", "hardware_monitor", "provider", "gpu", "notebook_oem"] as const;

function rejectFields(record: Record<string, unknown>, path: string, fields: readonly string[]) {
  for (const field of fields) {
    if (field in record) throw new ResponseDecodeError(`${path}.${field}`, "removed presentation field");
  }
}

function decodeCapability(value: unknown, path: string): ComponentCapability {
  const record = requireRecord(value, path);
  rejectFields(record, path, ["label"]);
  return {
    id: requireString(record.id, `${path}.id`),
    providerId: requireString(record.providerId, `${path}.providerId`),
    providerKind: requireOneOf(record.providerKind, `${path}.providerKind`, providerKinds)
  };
}

function decodeProvider(value: unknown, path: string): ComponentProviderState {
  const record = requireRecord(value, path);
  rejectFields(record, path, ["name"]);
  return {
    providerId: requireString(record.providerId, `${path}.providerId`),
    vendor: requireString(record.vendor, `${path}.vendor`),
    state: requireOneOf(record.state, `${path}.state`, providerStates),
    providerKind: requireOneOf(record.providerKind, `${path}.providerKind`, providerKinds),
    message: requireBackendMessage(record.message, `${path}.message`),
    runtimePath: requireNullable(record.runtimePath, `${path}.runtimePath`, requireString),
    capabilities: requireStringArray(record.capabilities, `${path}.capabilities`)
  };
}

export function decodeManagedComponent(value: unknown, path = "$"): ManagedComponent {
  const record = requireRecord(value, path);
  rejectFields(record, path, ["stateLabel"]);
  const definitionPath = `${path}.definition`;
  const definition = requireRecord(record.definition, definitionPath);
  rejectFields(definition, definitionPath, ["name", "category", "purpose", "installNote"]);
  return {
    definition: {
      id: requireString(definition.id, `${definitionPath}.id`),
      vendor: requireString(definition.vendor, `${definitionPath}.vendor`),
      sourcePageUrl: requireString(definition.sourcePageUrl, `${definitionPath}.sourcePageUrl`),
      externalTermsUrl: requireNullable(definition.externalTermsUrl, `${definitionPath}.externalTermsUrl`, requireString),
      managementRole: requireOneOf(definition.managementRole, `${definitionPath}.managementRole`, ["dependency", "support"]),
      isBundled: requireBoolean(definition.isBundled, `${definitionPath}.isBundled`),
      requiresElevation: requireBoolean(definition.requiresElevation, `${definitionPath}.requiresElevation`),
      requiresExternalTermsAcknowledgement: requireBoolean(definition.requiresExternalTermsAcknowledgement, `${definitionPath}.requiresExternalTermsAcknowledgement`),
      capabilities: requireArray(definition.capabilities, `${definitionPath}.capabilities`).map((item, index) => decodeCapability(item, `${definitionPath}.capabilities[${index}]`))
    },
    state: requireOneOf(record.state, `${path}.state`, states),
    installerSourceKind: requireOneOf(record.installerSourceKind, `${path}.installerSourceKind`, ["direct", "githubRelease", "manual"]),
    message: requireBackendMessage(record.message, `${path}.message`),
    installRoot: requireString(record.installRoot, `${path}.installRoot`),
    installerDirectory: requireString(record.installerDirectory, `${path}.installerDirectory`),
    installerPath: requireNullable(record.installerPath, `${path}.installerPath`, requireString),
    canDownload: requireBoolean(record.canDownload, `${path}.canDownload`),
    canInstall: requireBoolean(record.canInstall, `${path}.canInstall`),
    canVerify: requireBoolean(record.canVerify, `${path}.canVerify`),
    installerAvailable: requireBoolean(record.installerAvailable, `${path}.installerAvailable`),
    installed: requireBoolean(record.installed, `${path}.installed`),
    providerActive: requireBoolean(record.providerActive, `${path}.providerActive`),
    providers: requireArray(record.providers, `${path}.providers`).map((item, index) => decodeProvider(item, `${path}.providers[${index}]`))
  };
}

export function decodeManagedComponents(value: unknown): ManagedComponent[] {
  return requireArray(value, "$").map((item, index) => decodeManagedComponent(item, `$[${index}]`));
}

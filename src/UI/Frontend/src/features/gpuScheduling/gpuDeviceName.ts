export function resolveGpuDeviceName(
  index: number,
  hardwareName?: string | null,
  specializedAdapterName?: string | null
) {
  return normalizedName(hardwareName)
    ?? normalizedName(specializedAdapterName)
    ?? `GPU${index}`;
}

function normalizedName(value?: string | null) {
  const normalized = value?.trim();
  return normalized || null;
}

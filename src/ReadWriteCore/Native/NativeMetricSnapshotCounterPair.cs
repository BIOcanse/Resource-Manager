namespace ResourceManager.ReadWrite.Native;

public readonly record struct NativeMetricSnapshotCounterPair(ulong Primary, ulong Reference, ushort ReferenceFrequencyMhz = 0);

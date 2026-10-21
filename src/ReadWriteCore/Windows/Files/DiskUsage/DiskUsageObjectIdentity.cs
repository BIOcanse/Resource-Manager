namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>One file-system record incarnation on one volume. Path aliases share this identity;
/// recycled record numbers with a different sequence do not.</summary>
public readonly record struct DiskUsageObjectIdentity(ulong VolumeSerialNumber, long RecordNumber, ushort SequenceNumber);

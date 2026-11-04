using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerRuntimeIdentity
{
    public HostManagerRuntimeIdentity()
    {
        var identity = NativeFoundation.CaptureIdentity();
        InstanceId = identity.InstanceId;
        ProcessId = checked((int)identity.ProcessId);
        ProcessCreatedAt = new DateTime(checked((long)identity.ProcessCreatedUtcTicks), DateTimeKind.Utc);
    }

    public ulong InstanceId { get; }

    public int ProcessId { get; }

    public DateTime ProcessCreatedAt { get; }

    public long ProcessCreatedUtcTicks => ProcessCreatedAt.Ticks;

}

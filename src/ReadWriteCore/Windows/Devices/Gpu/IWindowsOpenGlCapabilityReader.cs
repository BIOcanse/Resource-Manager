using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>One context observation. The LUID and complete GL node mask are raw identity evidence from that context.
/// Values are publishable only when both exactly match the requested target.</summary>
public sealed record WindowsOpenGlAdapterCapabilityRead
{
    public WindowsOpenGlAdapterTarget Target { get; init; }
    public ulong? ObservedLuid { get; init; }
    public uint? ObservedNodeMask { get; init; }
    public string? Version { get; init; }
    public ImmutableArray<string>? Extensions { get; init; }
    public ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>? Limits { get; init; }

    public WindowsOpenGlAdapterCapabilityRead(WindowsOpenGlAdapterTarget Target, ulong? ObservedLuid,
        uint? ObservedNodeMask, string? Version, ImmutableArray<string>? Extensions)
        : this(Target, ObservedLuid, ObservedNodeMask, Version, Extensions, null) { }

    public WindowsOpenGlAdapterCapabilityRead(WindowsOpenGlAdapterTarget Target, ulong? ObservedLuid,
        uint? ObservedNodeMask, string? Version, ImmutableArray<string>? Extensions,
        ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>? Limits)
    {
        this.Target = Target;
        this.ObservedLuid = ObservedLuid;
        this.ObservedNodeMask = ObservedNodeMask;
        this.Version = Version;
        this.Extensions = Extensions;
        this.Limits = Limits;
    }

    public void Deconstruct(out WindowsOpenGlAdapterTarget Target, out ulong? ObservedLuid,
        out uint? ObservedNodeMask, out string? Version, out ImmutableArray<string>? Extensions)
    {
        Target = this.Target;
        ObservedLuid = this.ObservedLuid;
        ObservedNodeMask = this.ObservedNodeMask;
        Version = this.Version;
        Extensions = this.Extensions;
    }
}

/// <summary>A completed target enumeration can contain unavailable rows; a failed directory contains no rows.</summary>
public sealed record WindowsOpenGlCapabilityReadResult(
    bool DirectorySucceeded,
    ImmutableArray<WindowsOpenGlAdapterCapabilityRead> Targets)
{
    internal static WindowsOpenGlCapabilityReadResult Unavailable => new(false, []);
}

/// <summary>Explicit source for the Windows GL API capability view. Cached reads never invoke it.</summary>
public interface IWindowsOpenGlCapabilityReader
{
    WindowsOpenGlCapabilityReadResult Read(WindowsOpenGlCapabilityScope scope,
        WindowsOpenGlCapabilityFields selectedFields, CancellationToken cancellationToken);
}

/// <summary>
/// Caller-owned synchronous borrow of a current WGL context for one exact target. Execute must invoke the action
/// while that context is current on the executing thread and keep it alive and unchanged through the action.
/// Implementations may marshal work to their context thread or temporarily bind the context under their own
/// synchronization policy; they must restore any prior thread binding, must not create or destroy a context, and
/// must not run the action after the caller's context has been retired. A missing borrow or retired context should
/// throw; the native reader projects that target as unavailable. This is also the required seam for subscription
/// sampling: the caller must provide an executor that can borrow the context on its sampling thread, otherwise the
/// OpenGL fields are not sampleable through that subscription.
/// </summary>
public interface IWindowsOpenGlCurrentContextExecutor
{
    T Execute<T>(WindowsOpenGlAdapterTarget target, Func<T> action, CancellationToken cancellationToken);
}

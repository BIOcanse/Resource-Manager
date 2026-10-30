using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal readonly record struct KernelEtwNativeSessionIdentity(
    string Name,
    ulong Handle,
    Guid Guid,
    uint LogFileMode);

internal interface IKernelEtwNativeSessionControl
{
    KernelEtwNativeSessionIdentity Query(string sessionName);

    void SetSystemTraceFlags(ulong sessionHandle, ulong flags);

    void StopSession(ulong sessionHandle, string sessionName);
}

internal sealed class KernelEtwSessionKeywordControl
{
    internal static bool IsAccessDenied(Exception exception)
        => exception is UnauthorizedAccessException
            || exception is Win32Exception { NativeErrorCode: 5 }
            || exception.HResult == unchecked((int)0x80070005);

    internal const uint RealTimeMode = 0x00000100;
    internal const uint SystemLoggerMode = 0x02000000;
    private readonly IKernelEtwNativeSessionControl native;
    private readonly KernelEtwNativeSessionIdentity identity;

    internal KernelEtwSessionKeywordControl(
        string sessionName,
        IKernelEtwNativeSessionControl native)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (!ResourceManagerEtwSessionNames.TryParse(
                ResourceManagerEtwSessionNames.KernelPrefix, sessionName, out var owner)
            || owner != ResourceManagerEtwSessionNames.CurrentOwner)
        {
            throw new ArgumentException("The kernel session must belong to this process instance.", nameof(sessionName));
        }
        this.native = native;
        identity = native.Query(sessionName);
        ValidateIdentity(identity, sessionName);
    }

    public void UpdateKeywords(KernelTraceEventParser.Keywords keywords)
    {
        var flags = CompileSystemTraceFlags(keywords);
        VerifyOwnership();
        native.SetSystemTraceFlags(identity.Handle, flags);
    }

    public void StopSession()
    {
        try
        {
            VerifyOwnership();
            native.StopSession(identity.Handle, identity.Name);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 4201)
        {
            // Match TraceEvent.Stop: an absent session is already stopped.
        }
    }

    private void VerifyOwnership()
    {
        var current = native.Query(identity.Name);
        ValidateIdentity(current, identity.Name);
        if (current.Handle != identity.Handle || current.Guid != identity.Guid)
        {
            throw new InvalidOperationException("The owned kernel ETW session was replaced before its control operation.");
        }
    }

    internal static ulong CompileSystemTraceFlags(KernelTraceEventParser.Keywords keywords)
    {
        if (keywords == KernelTraceEventParser.Keywords.None)
        {
            throw new ArgumentOutOfRangeException(nameof(keywords), "An empty subscription set must stop the session.");
        }
        if ((keywords & ~KernelTraceEventParser.Keywords.All) != 0)
        {
            throw new NotSupportedException("TraceEvent extension keywords require their own enable protocol.");
        }
        // The native consumer has no parser-history fixup. Every dependency is
        // declared by its holder; an IRQ-only run must stay ImageLoad/ISR/DPC-only.
        return (uint)keywords;
    }

    private static void ValidateIdentity(KernelEtwNativeSessionIdentity candidate, string expectedName)
    {
        var requiredMode = RealTimeMode | SystemLoggerMode;
        if (!string.Equals(candidate.Name, expectedName, StringComparison.Ordinal)
            || candidate.Handle is 0 or ulong.MaxValue
            || candidate.Guid == Guid.Empty
            || (candidate.LogFileMode & requiredMode) != requiredMode)
        {
            throw new InvalidOperationException("The queried ETW session is not the expected real-time system logger.");
        }
    }
}

internal sealed unsafe class WindowsKernelEtwNativeSessionControl : IKernelEtwNativeSessionControl
{
    private const int NameCharacters = 1024;
    private const uint QueryControl = 0;
    private const uint StopControl = 1;
    private const uint SystemTraceEnableFlagsInfo = 4;
    private const uint TracedGuid = 0x00020000;

    public KernelEtwNativeSessionIdentity Query(string sessionName)
    {
        var buffer = ControlSession(0, sessionName, QueryControl);
        var propertiesSize = sizeof(EventTraceProperties);
        fixed (byte* storage = buffer)
        {
            var properties = (EventTraceProperties*)storage;
            var nameOffset = properties->LoggerNameOffset;
            if (nameOffset < propertiesSize || nameOffset >= buffer.Length || nameOffset % sizeof(char) != 0)
            {
                throw new InvalidDataException("The ETW session query returned an invalid name offset.");
            }
            var name = new ReadOnlySpan<char>(storage + nameOffset,
                Math.Min(NameCharacters, (buffer.Length - checked((int)nameOffset)) / sizeof(char)));
            var terminator = name.IndexOf('\0');
            if (terminator < 0) throw new InvalidDataException("The ETW session query returned an unterminated name.");
            return new(
                new string(name[..terminator]),
                properties->Wnode.HistoricalContext,
                properties->Wnode.Guid,
                properties->LogFileMode);
        }
    }

    public void StopSession(ulong sessionHandle, string sessionName)
        => ControlSession(sessionHandle, sessionName, StopControl);

    private static byte[] ControlSession(ulong sessionHandle, string sessionName, uint controlCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        if (sessionName.Length >= NameCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionName));
        }
        var propertiesSize = sizeof(EventTraceProperties);
        var buffer = new byte[propertiesSize + NameCharacters * sizeof(char) * 2];
        fixed (byte* storage = buffer)
        {
            var properties = (EventTraceProperties*)storage;
            properties->Wnode.BufferSize = checked((uint)buffer.Length);
            properties->Wnode.Flags = TracedGuid;
            properties->LoggerNameOffset = checked((uint)propertiesSize);
            properties->LogFileNameOffset = checked((uint)(propertiesSize + NameCharacters * sizeof(char)));
            sessionName.AsSpan().CopyTo(new Span<char>(storage + propertiesSize, NameCharacters));
            var status = ControlTraceW(sessionHandle, sessionName, properties, controlCode);
            if (status != 0) throw new Win32Exception(unchecked((int)status),
                controlCode == StopControl ? "Unable to stop the owned kernel ETW session." : "Unable to query the owned kernel ETW session.");
        }
        return buffer;
    }

    public void SetSystemTraceFlags(ulong sessionHandle, ulong flags)
    {
        var status = TraceSetInformation(sessionHandle, SystemTraceEnableFlagsInfo, in flags, sizeof(ulong));
        if (status != 0) throw new Win32Exception(unchecked((int)status), "Unable to update the owned kernel ETW keywords.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong Timestamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, EventTraceProperties* properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint TraceSetInformation(ulong sessionHandle, uint informationClass, in ulong flags, int informationLength);
}

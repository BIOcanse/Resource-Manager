using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

public enum WindowsSessionConnectionState
{
    Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init
}

/// <summary>A local WTS login's reported identity. LogonFileTime is the WTSINFOEX
/// user logon FILETIME, not connection time or an inferred boot-relative ordinal.
/// Connection state and session name are provider facts and can change during a login.</summary>
public sealed record WindowsLoginSession(uint SessionId, long LogonFileTime, string SessionName,
    string DomainName, string UserName, WindowsSessionConnectionState ConnectionState);

/// <summary>Passive stateless reader. Explicit Read enumerates all local WTS
/// sessions and queries their original WTSINFOEX level 1; no session action is exposed.
/// Null is a failed complete read, [] is a successful empty login enumeration.</summary>
public sealed class WindowsSessionInformationReader
{
    internal static (int EnvelopeBytes, int DataOffset, int LogonOffset) NativeLayout =>
        (Marshal.SizeOf<NativeInformationEnvelope>(), checked((int)Marshal.OffsetOf<NativeInformationEnvelope>(nameof(NativeInformationEnvelope.Data))),
            checked((int)Marshal.OffsetOf<NativeInformation>(nameof(NativeInformation.LogonTime))));
    public IReadOnlyList<WindowsLoginSession>? Read(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return null;
        nint buffer = 0;
        try
        {
            if (!WTSEnumerateSessionsW(0, 0, 1, out buffer, out var count)) return null;
            if (count > 131072 || (count != 0 && buffer == 0)) return null;
            var rows = new List<WindowsLoginSession>();
            var ids = new HashSet<uint>();
            var stride = Marshal.SizeOf<NativeSession>();
            for (uint index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var enumerated = Marshal.PtrToStructure<NativeSession>(buffer + checked((int)index * stride));
                if (!ids.Add(enumerated.SessionId)) return null;
                nint information = 0;
                try
                {
                    if (!WTSQuerySessionInformationW(0, enumerated.SessionId, 25, out information, out var bytes)
                        || information == 0 || bytes < Marshal.SizeOf<NativeInformationEnvelope>()) return null;
                    var envelope = Marshal.PtrToStructure<NativeInformationEnvelope>(information);
                    if (envelope.Level != 1) return null;
                    var fact = envelope.Data;
                    var row = Decode(enumerated.SessionId, fact.SessionId, fact.LogonTime,
                        fact.WinStationName ?? "", fact.Domain ?? "", fact.UserName ?? "", fact.State);
                    if (row is not null) rows.Add(row);
                }
                finally { if (information != 0) WTSFreeMemory(information); }
            }
            token.ThrowIfCancellationRequested();
            return rows.ToArray();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidDataException)
        { return null; }
        finally { if (buffer != 0) WTSFreeMemory(buffer); }
    }

    internal static WindowsLoginSession? Decode(uint enumeratedId, uint queriedId, long logonTime,
        string sessionName, string domain, string user, WindowsSessionConnectionState state)
    {
        // Enumeration is a directory; the queried record is the source of the value
        // and login identity. Refuse mixed/retired rows rather than publish an ordinal.
        if (enumeratedId != queriedId || !Enum.IsDefined(state)) throw new InvalidDataException("WTS session query identity/state mismatch.");
        if (user.Length == 0) return null; // Listener/service/empty pre-login slots are not user logins.
        if (logonTime <= 0 || logonTime > 2650467743999999999L)
            throw new InvalidDataException("WTS login lacks a valid source logon identity.");
        return new(queriedId, logonTime, sessionName, domain, user, state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSession
    { public uint SessionId; public nint SessionName; public WindowsSessionConnectionState State; }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeInformationEnvelope
    { public uint Level; public NativeInformation Data; }

    // Native WTSINFOEX_LEVEL1_W, default Windows packing: WCHAR arrays 33/21/18,
    // LARGE_INTEGER times align to 8. LogonTime explicitly uses UTC FILETIME units.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct NativeInformation
    {
        public uint SessionId;
        public WindowsSessionConnectionState State;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string? WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string? UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string? Domain;
        public long LogonTime, ConnectTime, DisconnectTime, LastInputTime, CurrentTime;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
    }
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(nint server, uint reserved, uint version, out nint sessions, out uint count);
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint sessionId, int informationClass, out nint information, out uint bytesReturned);
    [DllImport("wtsapi32.dll", ExactSpelling = true)] private static extern void WTSFreeMemory(nint memory);
}

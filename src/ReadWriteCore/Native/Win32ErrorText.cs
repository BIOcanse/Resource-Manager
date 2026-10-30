using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

/// <summary>
/// The system message text of a Win32 error code. Formatting it (FormatMessage) opens the
/// language pack's MUI resource file every time, so each (error code, thread UI language) is
/// looked up once and the text is kept; the text never changes for that key. A few dozen
/// distinct codes appear in practice, so the table stays at KiB scale.
/// </summary>
public static partial class Win32ErrorText
{
    private static readonly ConcurrentDictionary<(int Error, ushort Language), string> Messages = new();

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    public static string Get(int error) =>
        Messages.GetOrAdd((error, GetThreadUILanguage()), static key => new Win32Exception(key.Error).Message);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetThreadUILanguage();
}

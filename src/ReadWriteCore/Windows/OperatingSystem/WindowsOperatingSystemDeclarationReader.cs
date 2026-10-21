using System.Globalization;
using ResourceManager.ReadWrite.Windows.Platform;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

[Flags]
public enum WindowsOperatingSystemInformationFields : ushort
{
    None = 0, Uptime = 1, Build = 2, BootTime = 4,
    Activation = 8, Environment = 16, Startup = 32, Packages = 64,
    SessionId = 128, AcPower = 256, KeyboardRemap = 512,
    Features = 1024, ComponentPackages = 2048, DriverPackages = 4096,
    All = Uptime | Build | BootTime | Activation | Environment | Startup | Packages | SessionId | AcPower
        | KeyboardRemap | Features | ComponentPackages | DriverPackages
}

/// <summary>Direct operating-system provider facts. BootTime is LastBootUpTime,
/// not now-minus-uptime, last resume, user login or guaranteed cold boot time.</summary>
public sealed record WindowsOperatingSystemDeclarations(string? BuildDeclared, DateTimeOffset? BootTimeActual);

/// <summary>Stateless, explicitly selected OS singleton query using the existing
/// WMI timeout and cleanup transport. Construction does not read.</summary>
public sealed class WindowsOperatingSystemDeclarationReader
{
    private readonly TimeSpan timeout;
    private readonly Func<string, string, TimeSpan, QueryResult> query;
    public WindowsOperatingSystemDeclarationReader(TimeSpan queryTimeout)
        : this(queryTimeout, PlatformSensorWmiUtilities.QueryObjects) { }
    internal WindowsOperatingSystemDeclarationReader(TimeSpan queryTimeout, Func<string, string, TimeSpan, QueryResult> query)
    {
        if (queryTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(queryTimeout));
        timeout = queryTimeout; this.query = query;
    }
    public WindowsOperatingSystemDeclarations Read(WindowsOperatingSystemInformationFields selected)
    {
        if ((selected & ~WindowsOperatingSystemInformationFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(selected));
        var build = (selected & WindowsOperatingSystemInformationFields.Build) != 0;
        var boot = (selected & WindowsOperatingSystemInformationFields.BootTime) != 0;
        if (!build && !boot) return new(null, null);
        var columns = build && boot ? "BuildNumber, LastBootUpTime" : build ? "BuildNumber" : "LastBootUpTime";
        QueryResult result;
        try { result = query(@"root\CIMV2", $"SELECT {columns} FROM Win32_OperatingSystem", timeout); }
        catch (PlatformNotSupportedException) { return new(null, null); }
        if (result.Error is not null || result.Rows.Count != 1) return new(null, null);
        var row = result.Rows[0];
        return new(build && row.TryGetValue("BuildNumber", out var number) ? DecodeBuild(number) : null,
            boot && row.TryGetValue("LastBootUpTime", out var time) ? DecodeBootTime(time) : null);
    }
    internal static string? DecodeBuild(object? value)
        => value is string text && text.Length > 0 && text.All(static c => c >= '0' && c <= '9') ? text : null;

    internal static DateTimeOffset? DecodeBootTime(object? value)
    {
        if (value is not string text || text.Length != 25 || text[14] != '.' || text[21] is not ('+' or '-')) return null;
        if (!DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var calendar)
            || !int.TryParse(text.AsSpan(15, 6), NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
            || !int.TryParse(text.AsSpan(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var offsetMinutes)) return null;
        try
        {
            var offset = TimeSpan.FromMinutes(text[21] == '+' ? offsetMinutes : -offsetMinutes);
            return new DateTimeOffset(DateTime.SpecifyKind(calendar.AddTicks(microseconds * 10L), DateTimeKind.Unspecified), offset).ToUniversalTime();
        }
        catch (ArgumentException) { return null; }
    }
}

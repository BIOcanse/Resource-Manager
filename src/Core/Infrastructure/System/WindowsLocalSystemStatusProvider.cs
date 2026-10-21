using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.ReadWrite.Windows.SystemInformation;

namespace ResourceManager.App.Infrastructure.LocalSystem;

public sealed class WindowsLocalSystemStatusProvider : ILocalSystemStatusProvider
{
    public LocalSystemStatus GetStatus()
    {
        var capturedAt = DateTimeOffset.Now;
        var reading = WindowsMachineUptimeReader.Read();
        var uptime = reading?.Elapsed ?? TimeSpan.Zero;
        return new LocalSystemStatus(
            capturedAt,
            capturedAt - uptime,
            checked((long)(reading?.Seconds ?? 0)));
    }
}



using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Domain.LocalSystem;

namespace ResourceManager.App.Infrastructure.LocalSystem;

public sealed class WindowsLocalSystemStatusProvider : ILocalSystemStatusProvider
{
    public LocalSystemStatus GetStatus()
    {
        var capturedAt = DateTimeOffset.Now;
        var uptime = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64));
        return new LocalSystemStatus(
            capturedAt,
            capturedAt - uptime,
            (long)Math.Floor(uptime.TotalSeconds));
    }
}

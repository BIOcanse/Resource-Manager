using System.Management;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.MemoryProtection;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

/// <summary>Windows extrinsic volume events: no WITHIN query and no volume polling.</summary>
public sealed class WindowsHostMemoryProtectionVolumeNotifications : IHostMemoryProtectionVolumeNotifications
{
    public async IAsyncEnumerable<bool> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        // Notifications invalidate availability, so one pending refresh suffices: the consumer rereads
        // all configured locations. This capacity coalesces work; it does not delay or sample events.
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        // https://learn.microsoft.com/windows/win32/cimwin32prov/win32-volumechangeevent
        using var watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent"));
        watcher.EventArrived += Changed;
        watcher.Stopped += Stopped;
        try
        {
            watcher.Start();
            changes.Writer.TryWrite(true); // Close the startup/register race, through the same saved-settings path.
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken)) yield return change;
        }
        finally
        {
            watcher.EventArrived -= Changed;
            watcher.Stopped -= Stopped;
            watcher.Stop();
        }

        void Changed(object sender, EventArrivedEventArgs args)
        {
            using var notification = args.NewEvent;
            changes.Writer.TryWrite(true);
        }
        void Stopped(object sender, StoppedEventArgs args)
            => changes.Writer.TryComplete(new IOException($"volume-notifications-stopped:{args.Status}"));
    }
}

namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal sealed partial class AmdSmuPawnIoSession
{
    private static readonly TimeSpan PciLockTimeout = TimeSpan.FromSeconds(2);

    private static T WithPciLock<T>(Func<T> action)
    {
        try
        {
            using var mutex = new Mutex(false, @"Global\Access_PCI");
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(PciLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                return action();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return action();
        }
    }

    private static void WithPciLock(Action action)
    {
        WithPciLock(() =>
        {
            action();
            return true;
        });
    }
}

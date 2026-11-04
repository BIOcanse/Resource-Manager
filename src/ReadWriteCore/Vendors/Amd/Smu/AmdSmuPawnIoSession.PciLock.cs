namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

public sealed partial class AmdSmuPawnIoSession
{
    private static T WithPciLock<T>(TimeSpan timeout, Func<T> action)
    {
        using var mutex = new Mutex(false, @"Global\Access_PCI");
        return ExecuteWithPciLock(mutex, timeout, action);
    }

    internal static T ExecuteWithPciLock<T>(Mutex mutex, TimeSpan timeout, Func<T> action)
    {
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when the previous owner abandoned the mutex.
                acquired = true;
            }
            if (!acquired) throw new TimeoutException("Timed out waiting for the PCI bus access lock.");
            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void WithPciLock(TimeSpan timeout, Action action)
    {
        WithPciLock(timeout, () =>
        {
            action();
            return true;
        });
    }
}

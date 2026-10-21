namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

internal static class WindowsRawInputCaptureHostManager
{
    private static readonly object Gate = new();
    private static readonly Dictionary<WindowsRawInputDeviceKind, WindowsRawInputCaptureHost> Hosts = [];

    internal static IDisposable Add(WindowsRawInputDeviceKind kind, ulong selectedDeviceHandle,
        uint maximumRawInputBytes, Action<WindowsRawInputNativeEvent> onInput,
        Action<WindowsRawInputCaptureFault> onFault)
    {
        lock (Gate)
        {
            if (!Hosts.TryGetValue(kind, out var host))
            {
                host = new WindowsRawInputCaptureHost(kind);
                try
                {
                    host.Start();
                    Hosts.Add(kind, host);
                }
                catch
                {
                    host.StopAfterFailedStart();
                    throw;
                }
            }

            try
            {
                var id = host.AddListener(selectedDeviceHandle, maximumRawInputBytes, onInput, onFault);
                return new CaptureLease(kind, host, id);
            }
            catch
            {
                if (host.ListenerCount == 0)
                {
                    Hosts.Remove(kind);
                    host.StopAfterFailedStart();
                }
                throw;
            }
        }
    }

    private static void Release(WindowsRawInputDeviceKind kind, WindowsRawInputCaptureHost host, long id)
    {
        Exception? failure = null;
        lock (Gate)
        {
            if (!Hosts.TryGetValue(kind, out var current) || !ReferenceEquals(current, host)) return;
            try { host.VerifyRegistrationOwnership(); }
            catch (Exception exception)
            {
                failure = exception;
                host.NotifyAllFault(new(WindowsRawInputCaptureFailureCode.RegistrationOwnershipLost,
                    "This process no longer reports the session window as the Raw Input class owner."));
            }

            var last = host.RemoveListener(id);
            if (last)
            {
                Hosts.Remove(kind);
                // Keep Add serialized until the prior process-wide class registration
                // and message window are fully gone.
                try { host.Stop(); }
                catch (Exception exception) { failure ??= exception; }
            }
        }
        if (failure is not null) throw failure;
    }

    private sealed class CaptureLease(WindowsRawInputDeviceKind kind,
        WindowsRawInputCaptureHost host, long id) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Release(kind, host, id);
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ResourceManager.App.Infrastructure.Updates;

public interface IInternetConnectivity
{
    bool IsOnline { get; }
    event Action? Changed;
}

// NLM describes overall machine connectivity, including in a LocalSystem service;
// it does not depend on an interactive user's connection profile or session.
// Subscribe before reading the initial value so a transition cannot be missed.
public sealed class WindowsInternetConnectivity : IInternetConnectivity, IDisposable
{
    private readonly object gate = new();
    private object? manager;
    private IConnectionPoint? connection;
    private int cookie;
    private Sink? sink;
    private bool online;
    public event Action? Changed;

    public bool IsOnline
    {
        get
        {
            lock (gate)
            {
                if (manager is null)
                {
                    manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!)!;
                    try
                    {
                        var events = typeof(INetworkListManagerEvents).GUID;
                        ((IConnectionPointContainer)manager).FindConnectionPoint(ref events, out connection);
                        sink = new(this);
                        connection!.Advise(sink, out cookie);
                        online = ((INetworkListManager)manager).IsConnectedToInternet;
                    }
                    catch { Dispose(); throw; }
                }
                return online;
            }
        }
    }

    private void SetConnectivity(int connectivity)
    {
        lock (gate)
        {
            var value = (connectivity & (0x40 | 0x400)) != 0; // IPv4/IPv6 Internet
            if (online == value) return;
            online = value;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { if (cookie != 0) connection?.Unadvise(cookie); }
            finally
            {
                cookie = 0;
                if (connection is not null) Marshal.ReleaseComObject(connection);
                if (manager is not null) Marshal.ReleaseComObject(manager);
                connection = null;
                manager = null;
                sink = null;
            }
        }
    }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface INetworkListManager
    {
        [DispId(5)] bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    }

    [ComVisible(true), Guid("DCB00001-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INetworkListManagerEvents
    {
        void ConnectivityChanged(int connectivity);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Sink(WindowsInternetConnectivity owner) : INetworkListManagerEvents
    {
        public void ConnectivityChanged(int connectivity) => owner.SetConnectivity(connectivity);
    }
}

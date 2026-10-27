using System.Management;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class UniwillEcBridgeTests
{
    [Fact]
    public void MissingMethodMetadataIsResolvedOnceWithoutInvokingHardware()
    {
        var queries = 0;
        using var session = new UniwillEcBridge.WmiSession(new ManagementObject(), () => { queries++; return null; });
        for (var index = 0; index < 100; index++) Assert.Null(session.ResolveParameters());
        Assert.Equal(1, queries);
    }

    [Fact]
    public void ReadReusesOneOwnedSessionForItsAgreeingCalls()
    {
        var session = new FakeSession(_ => 0x5A);
        var opens = 0;
        using (var bridge = new UniwillEcBridge(() =>
        {
            opens++;
            return session;
        }))
        {
            Assert.True(bridge.IsAvailable);
            Assert.Equal((byte)0x5A, bridge.Read(0x0740));
            Assert.Equal(1, opens);
            Assert.Equal(3, session.Arguments.Count);
            Assert.All(session.Arguments, argument => Assert.Equal((1UL << 40) | 0x0740, argument));
        }

        Assert.True(session.Disposed);
    }

    [Fact]
    public void MethodFailureDisposesSessionAndAllowsTheExistingRebindPath()
    {
        var failed = new FakeSession(_ => throw new ManagementException("injected failure"));
        var recovered = new FakeSession(_ => 0x21);
        var sessions = new Queue<IUniwillEcWmiSession?>([failed, recovered]);
        using var bridge = new UniwillEcBridge(() => sessions.Dequeue());

        Assert.Null(bridge.Read(0x0740));
        Assert.True(failed.Disposed);
        Assert.Equal((byte)0x21, bridge.Read(0x0740));
        Assert.False(recovered.Disposed);
    }

    private sealed class FakeSession(Func<ulong, uint?> invoke) : IUniwillEcWmiSession
    {
        public List<ulong> Arguments { get; } = [];
        public bool Disposed { get; private set; }

        public uint? Invoke(ulong data)
        {
            Arguments.Add(data);
            return invoke(data);
        }

        public void Dispose() => Disposed = true;
    }
}

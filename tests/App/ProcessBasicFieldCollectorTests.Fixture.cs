using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using NetworkTests = Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessBasicFieldCollectorTests
{
    internal static readonly string[] Names = ProcessMetricFieldRegistry.Definitions.Where(static field => field.DatasetId == SamplingDatasetIds.ProcessBasicMetrics && (int)field.Code >= (int)ProcessMetricFieldCode.CpuTime && (int)field.Code <= (int)ProcessMetricFieldCode.HandleCount).Select(static field => field.Name).ToArray();
    internal static readonly string[] NetworkNames = ProcessMetricFieldRegistry.Definitions.Where(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic).Select(static field => field.Name).ToArray();
    internal static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    internal static readonly ulong Birth = ulong.Parse(NetworkTests.Birth, CultureInfo.InvariantCulture);
    internal static ResourceBreakdownSampleRequest Request(params string[] names) => new(names.Length == 0 ? Names : names, new Dictionary<string, string>(), ProcessSampleDetailLevel.SmartSchedulingLite);
    internal static NetworkAttributionSnapshot Network(DateTimeOffset captured) => new(captured, new Dictionary<int, NetworkProcessAttribution>(), new("etw-network-tcpip", NetworkAttributionProviderStates.Running, "fixture"))
    { ObservedTraffic = new(captured, 2000, [new(Environment.ProcessId, Birth.ToString(CultureInfo.InvariantCulture), "9007199254740993", "7", 0, 0)]) };
    internal static NetworkTests.Fixture Fixture(Api api, NetworkTests.NetworkReader? network = null, Func<Process[]>? inventory = null, TimeProvider? clock = null)
        => new(network ?? new NetworkTests.NetworkReader(_ => throw new InvalidOperationException("unexpected network source")), inventory, new WindowsProcessBasicMetricReader(api, clock));
    internal static ProcessMetricFieldValue Value(ResourceBreakdownSnapshot snapshot, string field)
        => Assert.Single(Assert.Single(snapshot.ProcessFields!.Processes).Values, item => item.Field == field);
    internal static JsonDocument Sdk()
    {
        var assembly = typeof(ProcessBasicFieldCollectorTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), item => item.EndsWith(".Fixtures.Memory.windows-sdk-process-basic.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!; return JsonDocument.Parse(stream);
    }
    internal static T Structure<T>(byte[] bytes) where T : struct
    {
        Assert.Equal(Marshal.SizeOf<T>(), bytes.Length); var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(pinned.AddrOfPinnedObject()); } finally { pinned.Free(); }
    }

    internal sealed class Api : IWindowsProcessBasicMetricApi
    {
        internal string Fault = "";
        internal Action? AfterIo;
        internal ProcessMemoryObservation? MemoryValue = new(9007199254740993, 9007199254740997, ulong.MaxValue, 3);
        internal ProcessIoObservation? IoValue = new(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 9007199254740993, 0, ulong.MaxValue);
        internal uint? Handles = 0;
        internal ulong Kernel = 75000000, User = 22500000;
        internal int Opens, TimeCalls, MemoryCalls, IoCalls, HandleCalls, Closes;
        internal int ImageCalls, SidCalls, ParentCalls;
        internal string? Image = @"C:\Recorded\app.exe", Sid = "S-1-5-21-1-2-3-1001";
        internal uint? Parent = 0;
        internal readonly List<bool> PrivateRequests = [];
        public IntPtr Open(int processId)
        {
            Assert.Equal(Environment.ProcessId, processId); Interlocked.Increment(ref Opens);
            return Fault == "open" ? IntPtr.Zero : Fault == "invalid-handle" ? new IntPtr(-1) : new IntPtr(42);
        }
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        {
            // One creation check per handle read; the reader no longer re-reads times after its queries.
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref TimeCalls);
            creation = Fault == "identity-before" ? Birth + 1 : Birth;
            kernel = Kernel; user = User;
            return Fault != "times-before";
        }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref MemoryCalls); lock (PrivateRequests) PrivateRequests.Add(privateWorkingSet);
            if (Fault == "throw-memory") throw new COMException("memory failed");
            var value = Fault == "memory" ? null : MemoryValue;
            return privateWorkingSet ? value : value is null ? null : value with { PrivateWorkingSet = null };
        }
        public ProcessIoObservation? Io(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref IoCalls);
            if (Fault == "throw-io") throw new COMException("IO failed"); AfterIo?.Invoke(); return Fault == "io" ? null : IoValue;
        }
        public uint? HandleCount(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref HandleCalls);
            if (Fault == "throw-handles") throw new COMException("handles failed"); return Fault == "handles" ? null : Handles;
        }
        public bool Close(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref Closes);
            if (Fault == "throw-close") throw new COMException("close failed"); return Fault != "close";
        }
        public string? ImagePath(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref ImageCalls);
            if (Fault == "throw-image") throw new COMException("image failed"); return Fault == "image" ? null : Image;
        }
        public string? UserSid(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref SidCalls);
            if (Fault == "throw-sid") throw new COMException("SID failed"); return Fault == "sid" ? null : Sid;
        }
        public uint? ParentPid(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle); Interlocked.Increment(ref ParentCalls);
            if (Fault == "throw-parent") throw new COMException("parent failed"); return Fault == "parent" ? null : Parent;
        }
    }
}


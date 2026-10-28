using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class OpenClCapabilitySubscriptionConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var target = new WindowsOpenClAdapterTarget(0x0000_0001_0000_002A, 1);
        var scope = new WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields.All, [target]);
        var reader = new RecordedProvider();
        var access = owner.CreateOpenClCapabilityInformation(scope, reader);
        var backend = access.Backend;
        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        var version = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_opencl_device_version").Field;
        var extension = catalog.Fields.Single(static field => field.Field.FieldName == "gpu_all_opencl_extension"
            && field.Field.DeviceIdentity.EndsWith(
                "token-utf8-hex:636C5F6B68725F6465766963655F75756964", StringComparison.Ordinal)).Field;
        Require(reader.Requests.Single() == WindowsOpenClCapabilityFields.All, "initial explicit field scope");

        reader.Mode = RecordedMode.DirectoryUnavailable;
        var borrow = access.BorrowSubscriptionCapture();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        var workerFailures = new ConcurrentQueue<string>();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => workerFailures.Enqueue($"{failure.Stage}: {failure.Error.Message}"));

        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-opencl-capabilities", [version, extension],
            TimeSpan.FromSeconds(1), new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator())
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "selected unavailable read was delivered");
            var readings = stream.Current.ToDictionary(static reading => reading.Field);
            Require(readings[version].Status == FieldObservationStatus.Unavailable && readings[version].Value is null,
                "directory failure clears the selected version value");
            Require(readings[extension].Status == FieldObservationStatus.Unavailable && readings[extension].Value is null,
                "directory failure clears the selected extension value");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[version], backend.ReadCached(version));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[extension], backend.ReadCached(extension));
            Require(reader.Requests.Last() == WindowsOpenClCapabilityFields.All,
                "subscription captures only its selected fields");

            var stateBeforeCancel = owner.ReadState();
            reader.BlockNextReadUntilCanceled();
            var pending = stream.MoveNextAsync().AsTask();
            await reader.BlockEntered.WaitAsync(timeout.Token);
            await composition.StopAsync().WaitAsync(timeout.Token);
            Require(ReferenceEquals(stateBeforeCancel, owner.ReadState()),
                "canceled pending OpenCL capture did not publish to the shared current");
            Require(!await pending.WaitAsync(timeout.Token), "stopped role-2 stream ended without a late value");
        }
        Require(workerFailures.IsEmpty, "recorded unavailable result and canceled read were not worker faults");

        reader.Mode = RecordedMode.Current;
        await composition.StartAsync();
        using (var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]))
        {
            var beforeDemand = reader.ReadCount;
            var lease = composition.Acquire("last-recorded-opencl-demand", [version, extension], TimeSpan.FromMilliseconds(250));
            using (lease)
            using (var demandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await publication.WaitAsync(demandTimeout.Token);
                Require(reader.ReadCount > beforeDemand, "the last explicit demand caused a recorded capture");
                Require(backend.ReadCached(version).Status == FieldObservationStatus.Current
                    && backend.ReadCached(version).Value?.Text == "OpenCL 3.0 subscription-recorded",
                    "successful raw device version is current in the shared owner");
                Require(backend.ReadCached(extension).Status == FieldObservationStatus.Current
                    && backend.ReadCached(extension).Value?.Text == "cl_khr_device_uuid",
                    "successful extension token is current in the shared owner");
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(350));
        var readsAfterLastDemand = reader.ReadCount;
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        Require(reader.ReadCount == readsAfterLastDemand, "withdrawing the last resource demand stopped further reads");
        await composition.StopAsync();
        Require(workerFailures.IsEmpty, "recorded role-2 OpenCL subscription completed without worker faults");
        Console.WriteLine("[OPENCL-CAPABILITY-SUBSCRIPTION] recorded directory failure/null, cancel/no-commit, successful same-owner reads, last-demand withdrawal, and explicit Start/Stop passed; no OpenCL native read.");
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"OpenCL subscription consumer failed: {label}.");
    }

    private enum RecordedMode { Current, DirectoryUnavailable }

    private sealed class RecordedProvider : IWindowsOpenClCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsOpenClCapabilityFields> requests = new();
        private int blockNextRead;
        private TaskCompletionSource blockEntered = CompletedSignal();
        private int readCount;
        public RecordedMode Mode { get; set; }
        public IReadOnlyList<WindowsOpenClCapabilityFields> Requests => requests.ToArray();
        public int ReadCount => Volatile.Read(ref readCount);
        public Task BlockEntered => Volatile.Read(ref blockEntered).Task;

        public void BlockNextReadUntilCanceled()
        {
            Volatile.Write(ref blockEntered, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref blockNextRead, 1);
        }

        public WindowsOpenClCapabilityReadResult Read(WindowsOpenClCapabilityScope scope,
            WindowsOpenClCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            Interlocked.Increment(ref readCount);
            if (Interlocked.Exchange(ref blockNextRead, 0) != 0)
            {
                Volatile.Read(ref blockEntered).TrySetResult();
                if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The subscription composition did not cancel its recorded pending OpenCL read.");
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Mode == RecordedMode.DirectoryUnavailable)
                return new(false, ImmutableArray<WindowsOpenClDeviceCapabilityRead>.Empty);
            var identity = new WindowsOpenClDeviceIdentity(scope.Targets.Single(),
                "00112233445566778899AABBCCDDEEFF", "FFEEDDCCBBAA99887766554433221100");
            return new(true, [new(identity,
                (selectedFields & WindowsOpenClCapabilityFields.DeviceVersion) != 0 ? "OpenCL 3.0 subscription-recorded" : null,
                (selectedFields & WindowsOpenClCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("cl_khr_device_uuid", "cl_khr_fp64") : null)]);
        }

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult();
            return signal;
        }
    }
}

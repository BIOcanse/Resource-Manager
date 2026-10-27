using System.Diagnostics;
using System.Globalization;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.KernelLeak;

public sealed class WindowsKernelLeakAttributionReader : IKernelLeakAttributionReader
{
    private const int MaximumServiceBufferBytes = 1024 * 1024, MaximumServices = 16384, MaximumTextCharacters = 32768;
    private readonly Func<int, long, string?> readPath;
    private readonly Func<int, ProcessCreationRead> readIdentity;
    private readonly Func<string, string?> readProduct;
    private readonly Func<CancellationToken, Dictionary<int, IReadOnlyList<string>>?> readServices;
    private readonly ILogger<WindowsKernelLeakAttributionReader>? logger;
    public WindowsKernelLeakAttributionReader(ILogger<WindowsKernelLeakAttributionReader>? logger = null)
    {
        var reader = new WindowsProcessBasicMetricReader();
        readPath = (pid, birth) => reader.Read(pid, birth, ProcessMetricReadMask.ImagePath).ImagePath;
        readIdentity = WindowsProcessCreationQuery.Read;
        readProduct = path => FileVersionInfo.GetVersionInfo(path).ProductName;
        readServices = ReadServices;
        this.logger = logger;
    }
    // Recorded OS query outcomes exercise the real attribution composition, including product read failures.
    internal WindowsKernelLeakAttributionReader(Func<int, long, string?> path, Func<int, ProcessCreationRead> identity,
        Func<string, string?> product, Func<CancellationToken, Dictionary<int, IReadOnlyList<string>>?> services)
        => (readPath, readIdentity, readProduct, readServices) = (path, identity, product, services);
    public IReadOnlyList<KernelLeakHolder> ReadBatch(IReadOnlyList<KernelLeakHolder> holders, CancellationToken cancellationToken = default)
    {
        if (holders.Count > MaximumServices) throw new OverflowException("Kernel leak attribution capacity exceeded.");
        if (holders.Count == 0) return [];
        var services = readServices(cancellationToken);
        var result = new KernelLeakHolder[holders.Count];
        for (var index = 0; index < holders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var holder = holders[index];
            var birth = long.Parse(holder.StartKey, CultureInfo.InvariantCulture);
            var path = readPath(holder.ProcessId, birth);
            var image = KernelLeakImage.Unavailable;
            if (path is { Length: > 0 and <= MaximumTextCharacters })
            {
                try
                {
                    var product = readProduct(path); // null is genuine absence; read failure makes the image unavailable.
                    if (product?.Length > MaximumTextCharacters) throw new InvalidDataException("Kernel leak product name capacity exceeded.");
                    if (string.IsNullOrWhiteSpace(product)) product = null;
                    image = KernelLeakImage.Current(Path.GetFileNameWithoutExtension(path), path, product);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
                {
                    logger?.LogWarning(error, "Kernel leak image metadata unavailable for PID {ProcessId}.", holder.ProcessId);
                }
            }
            // Reused PID also proves the original holder ended. Ended status never carries stale metadata.
            var identity = readIdentity(holder.ProcessId);
            var ended = identity.Status == ProcessReadStatus.Exited || identity.Creation is { } actual && actual != birth;
            var valid = !ended && identity.Creation == birth;
            result[index] = holder with
            {
                Image = ended ? KernelLeakImage.ProcessEnded : valid ? image : KernelLeakImage.Unavailable,
                Services = ended ? new(KernelLeakMetadataStatus.ProcessEnded, [])
                    : valid && services is not null ? new(KernelLeakMetadataStatus.Current, services.GetValueOrDefault(holder.ProcessId) ?? [])
                    : new(KernelLeakMetadataStatus.Unavailable, [])
            };
        }
        return result;
    }
    private static Dictionary<int, IReadOnlyList<string>>? ReadServices(CancellationToken cancellationToken)
    {
        var capture = new ResourceManager.ReadWrite.Windows.Services.WindowsServiceReader().Read(
            cancellationToken: cancellationToken, maximumBufferBytes: MaximumServiceBufferBytes, maximumServices: MaximumServices);
        return capture.Available ? capture.Services.Where(service => service.ProcessId is > 0 and <= int.MaxValue)
            .GroupBy(service => (int)service.ProcessId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(service => service.ServiceName).ToArray()) : null;
    }
}

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
    private readonly WindowsProcessBasicMetricReader reader = new();
    public IReadOnlyList<KernelLeakHolder> ReadBatch(IReadOnlyList<KernelLeakHolder> holders, CancellationToken cancellationToken = default)
    {
        if (holders.Count > 16384) throw new OverflowException("Kernel leak attribution capacity exceeded.");
        if (holders.Count == 0) return [];
        var services = ReadServices(cancellationToken);
        var result = new KernelLeakHolder[holders.Count];
        for (var index = 0; index < holders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var holder = holders[index];
            var birth = long.Parse(holder.StartKey, CultureInfo.InvariantCulture);
            var path = reader.Read(holder.ProcessId, birth, ProcessMetricReadMask.ImagePath).ImagePath;
            string? name = null, product = null;
            if (path is { Length: > 0 and <= MaximumTextCharacters })
            {
                name = Path.GetFileNameWithoutExtension(path);
                try { product = FileVersionInfo.GetVersionInfo(path).ProductName; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { }
                if (product?.Length > MaximumTextCharacters) product = null;
            }
            else path = null;
            // Metadata cannot be attached to a reused PID. Failure affects attribution, not growth evidence.
            var identity = WindowsProcessCreationQuery.Read(holder.ProcessId);
            var valid = identity.Creation == birth;
            result[index] = holder with
            {
                Name = valid ? name : null, ImagePath = valid ? path : null, ProductName = valid ? product : null,
                ServiceNames = valid && services is not null ? services.GetValueOrDefault(holder.ProcessId) ?? [] : [],
                ImageStatus = identity.Status == ProcessReadStatus.Exited ? KernelLeakMetadataStatus.ProcessEnded
                    : valid && path is not null ? KernelLeakMetadataStatus.Current : KernelLeakMetadataStatus.Unavailable,
                ServiceStatus = valid && services is not null ? KernelLeakMetadataStatus.Current : KernelLeakMetadataStatus.Unavailable
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
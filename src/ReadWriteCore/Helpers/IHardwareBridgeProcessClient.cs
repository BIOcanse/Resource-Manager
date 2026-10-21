using System.Text.Json;
namespace ResourceManager.ReadWrite.Helpers;
/// <summary>The existing helper's SendAsync seam. Implementations retain their own original transport state.</summary>
public interface IHardwareBridgeProcessClient : IDisposable
{
    bool IsInstalled { get; }
    string? UnavailableReason { get; }
    Task<JsonElement?> SendAsync(string operation, IReadOnlyDictionary<string, object?>? arguments, CancellationToken token);
}

using System.Text.Json;
namespace ResourceManager.ReadWrite.Helpers;
/// <summary>The helper transport owns its session. Repeated describe requests reuse that session's facts;
/// a newly started helper describes before its first operation. Read requests always perform the requested reading.</summary>
public interface IHardwareBridgeProcessClient : IDisposable
{
    bool IsInstalled { get; }
    string? UnavailableReason { get; }
    Task<JsonElement?> SendAsync(string operation, IReadOnlyDictionary<string, object?>? arguments, CancellationToken token);
}

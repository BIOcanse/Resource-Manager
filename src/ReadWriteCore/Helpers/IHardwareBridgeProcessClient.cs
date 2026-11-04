using System.Text.Json;
namespace ResourceManager.ReadWrite.Helpers;
/// <summary>Current-run commands and readings. Each reading consumer opens a subscription for its lifetime.</summary>
public interface IHardwareBridgeProcessClient : IDisposable
{
    bool IsInstalled { get; }
    string? UnavailableReason { get; }
    Task<JsonElement?> SendAsync(string operation, IReadOnlyDictionary<string, object?>? arguments, CancellationToken token);
    HardwareBridgeReadingsSnapshot? ReadCurrentReadings();
    IDisposable SubscribeReadings();
}

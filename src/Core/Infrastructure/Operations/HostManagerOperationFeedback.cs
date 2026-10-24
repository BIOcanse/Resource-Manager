using System.Text.Json;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.Operations;

// Keep the operation journal's text payload format; decode once at the HTTP boundary.
internal sealed record HostManagerOperationFeedback(
    string? ResultCode,
    BackendMessage Message,
    string? Diagnostic = null)
{
    private const string Prefix = "operation-feedback.v1:";

    internal static string Result(string code, BackendMessage message)
        => Prefix + JsonSerializer.Serialize(new HostManagerOperationFeedback(code, message));

    internal static string Failure(Exception error)
        => Reason(error switch
        {
            HttpRequestException => BackendMessageCodes.Operation.NetworkFailure,
            FileNotFoundException => BackendMessageCodes.Operation.FileUnavailable,
            UnauthorizedAccessException => BackendMessageCodes.Operation.AccessDenied,
            InvalidOperationException or ArgumentException => BackendMessageCodes.Operation.RequestRejected,
            IOException => BackendMessageCodes.Operation.IoFailure,
            _ => BackendMessageCodes.Operation.UnexpectedFailure
        }, error.Message);

    internal static string Reason(byte code, string? diagnostic = null)
        => Prefix + JsonSerializer.Serialize(new HostManagerOperationFeedback(
            null, BackendMessage.Create(BackendMessageDomains.Operation, code), diagnostic));

    internal static HostManagerOperationFeedback? Read(string? value)
        => value?.StartsWith(Prefix, StringComparison.Ordinal) == true
            ? JsonSerializer.Deserialize<HostManagerOperationFeedback>(value[Prefix.Length..])
            : null;
}

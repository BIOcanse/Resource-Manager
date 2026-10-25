using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.Operations;

// Structured owner facts inside the coordinator's opaque canonical payload catalog.
internal sealed record HostManagerOperationFeedback(
    string? ResultCode,
    BackendMessage Message,
    string? Diagnostic = null)
{
    private const string Prefix = "operation-feedback.v2:";

    internal string Encode()
        => Prefix + JsonSerializer.Serialize(this, OperationFeedbackJsonContext.Default.HostManagerOperationFeedback);

    internal static string Result(string code, BackendMessage message)
        => Prefix + JsonSerializer.Serialize(new HostManagerOperationFeedback(code, message), OperationFeedbackJsonContext.Default.HostManagerOperationFeedback);

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

    internal static string Failure(BackendMessage message)
        => Prefix + JsonSerializer.Serialize(new HostManagerOperationFeedback(
            null, message), OperationFeedbackJsonContext.Default.HostManagerOperationFeedback);

    internal static string Reason(byte code, string? diagnostic = null)
        => Prefix + JsonSerializer.Serialize(new HostManagerOperationFeedback(
            null, BackendMessage.Create(BackendMessageDomains.Operation, code), diagnostic), OperationFeedbackJsonContext.Default.HostManagerOperationFeedback);

    internal static HostManagerOperationFeedback? Read(string? value)
        => value?.StartsWith(Prefix, StringComparison.Ordinal) == true
            ? JsonSerializer.Deserialize(value[Prefix.Length..], OperationFeedbackJsonContext.Default.HostManagerOperationFeedback)
            : null;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HostManagerOperationFeedback))]
internal partial class OperationFeedbackJsonContext : JsonSerializerContext;

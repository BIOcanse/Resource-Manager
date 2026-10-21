using System.Text.Json;

namespace ResourceManager.Shared.Runtime;

public sealed record UserShellExecuteRequest(string FileName, string Arguments, string? Verb = null)
{
    public const string Argument = "--user-shell-execute";
    private const int MaximumEncodedLength = 24_000;

    public string Encode()
    {
        Validate();
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
        if (encoded.Length > MaximumEncodedLength) throw new InvalidDataException("User launch request exceeds the command-line limit.");
        return encoded;
    }

    public static UserShellExecuteRequest Decode(string encoded)
    {
        if (encoded.Length > MaximumEncodedLength) throw new InvalidDataException("User launch request exceeds the command-line limit.");
        var request = JsonSerializer.Deserialize<UserShellExecuteRequest>(Convert.FromBase64String(encoded))
            ?? throw new InvalidDataException("User launch request is empty.");
        request.Validate();
        return request;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(FileName) || FileName.Contains('\0') || Arguments is null || Arguments.Contains('\0'))
            throw new InvalidDataException("User launch request contains an invalid command.");
        if (Verb is not null && Verb is not ("runas" or "properties"))
            throw new InvalidDataException("User launch request contains an unsupported shell verb.");
    }
}

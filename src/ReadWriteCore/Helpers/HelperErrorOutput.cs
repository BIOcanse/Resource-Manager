using System.Text;

namespace ResourceManager.ReadWrite.Helpers;

public class HelperErrorOutput
{
    private readonly HelperErrorOutputOptions options;
    private readonly object gate = new();
    private readonly StringBuilder first = new(), tail = new();
    public HelperErrorOutput(TextReader reader, HelperErrorOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ReadBufferSize <= 0 || options.FirstCharacterLimit < 0 || options.TailCharacterLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        Completion = DrainAsync(reader);
    }
    public Task Completion { get; }
    public string Detail { get { lock (gate) return first + "\n" + tail; } }
    public string FailureReason => Detail.Contains(options.RuntimeName, StringComparison.OrdinalIgnoreCase)
        && Detail.Contains(options.RuntimeVersionMarker, StringComparison.Ordinal) ? options.MissingRuntimeReason : options.StartupFailedReason;

    private async Task DrainAsync(TextReader reader)
    {
        var buffer = new char[options.ReadBufferSize];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            {
                lock (gate)
                {
                    first.Append(buffer, 0, Math.Min(count, options.FirstCharacterLimit - first.Length));
                    tail.Append(buffer, 0, count);
                    if (tail.Length > options.TailCharacterLimit) tail.Remove(0, tail.Length - options.TailCharacterLimit);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}

public sealed record HelperErrorOutputOptions(int ReadBufferSize, int FirstCharacterLimit,
    int TailCharacterLimit, string RuntimeName, string RuntimeVersionMarker,
    string MissingRuntimeReason, string StartupFailedReason);

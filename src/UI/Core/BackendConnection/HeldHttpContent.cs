using System.Net;
using System.Net.Http;

namespace ResourceManager.NativeUi;

/// <summary>
/// A backend response body that keeps the backend session held until the response is disposed.
/// Reading it hands out the backend's own stream: the loopback proxy copies subscription streams
/// (/api/subscriptions/stream and other indefinite responses) chunk by chunk, so the body must never
/// be buffered to its end, and the page's cancellation must reach the backend request.
/// HttpContent's default read path buffers the whole body (LoadIntoBufferAsync) and its
/// token-less SerializeToStreamAsync drops cancellation, so every read path is forwarded here.
/// </summary>
internal sealed class HeldHttpContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly IDisposable hold;

    internal HeldHttpContent(HttpContent inner, IDisposable hold)
    {
        this.inner = inner;
        this.hold = hold;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
        => inner.ReadAsStreamAsync();

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => inner.ReadAsStreamAsync(cancellationToken);

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        => inner.ReadAsStream(cancellationToken);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => inner.CopyToAsync(stream);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => inner.CopyToAsync(stream, cancellationToken);

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => inner.CopyTo(stream, context, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = inner.Headers.ContentLength.GetValueOrDefault();
        return inner.Headers.ContentLength.HasValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            hold.Dispose();
        }
        base.Dispose(disposing);
    }
}

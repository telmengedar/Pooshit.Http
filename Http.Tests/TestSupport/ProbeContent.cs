using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Http.Tests.TestSupport;

/// <summary>
/// http content that reports how many bytes were pulled through <see cref="SerializeToStreamAsync(Stream,TransportContext)"/>,
/// optionally declares no content length, and optionally holds delivery open until <see cref="Release"/> is called
/// </summary>
public class ProbeContent : HttpContent {
    readonly byte[] payload;
    readonly bool declaresLength;
    readonly TaskCompletionSource<bool>? gate;

    public ProbeContent(byte[] payload, bool declaresLength = true, bool gated = false) {
        this.payload = payload;
        this.declaresLength = declaresLength;
        gate = gated ? new TaskCompletionSource<bool>() : null;
    }

    public long BytesRead { get; private set; }

    public bool Disposed { get; private set; }

    public void Release() => gate?.TrySetResult(true);

    protected override void Dispose(bool disposing) {
        if (disposing)
            Disposed = true;
        base.Dispose(disposing);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) {
        if (gate != null)
            await gate.Task;
        await stream.WriteAsync(payload, 0, payload.Length);
        BytesRead += payload.Length;
    }

    protected override bool TryComputeLength(out long length) {
        length = payload.Length;
        return declaresLength;
    }
}

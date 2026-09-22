using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Http.Tests.TestSupport;

/// <summary>
/// http content which delivers its payload once and fails every later delivery with a given error
/// </summary>
public class SingleUseContent : HttpContent {
    readonly byte[] payload;
    readonly Exception exhausted;
    int deliveries;

    /// <summary>
    /// creates a new <see cref="SingleUseContent"/>
    /// </summary>
    /// <param name="payload">bytes delivered on the first serialization</param>
    /// <param name="exhausted">error raised from every serialization after the first</param>
    public SingleUseContent(byte[] payload, Exception exhausted) {
        this.payload = payload;
        this.exhausted = exhausted;
    }

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) {
        if (++deliveries > 1)
            throw exhausted;

        await stream.WriteAsync(payload, 0, payload.Length);
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) {
        length = payload.Length;
        return true;
    }
}

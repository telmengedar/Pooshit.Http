using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Pooshit.Json;

namespace Pooshit.Http.Encodings;

/// <summary>
/// json request body which is buffered while it fits a size cap and serialized to the wire once it does not
/// </summary>
class JsonContent : HttpContent {
    const int bufferLimit = 85000;
    readonly object data;
    readonly JsonOptions options;
    readonly byte[] buffer;
    readonly int count;
    readonly bool streamed;

    /// <summary>
    /// creates a new <see cref="JsonContent"/>
    /// </summary>
    /// <param name="data">object to serialize</param>
    /// <param name="options">options to use when serializing</param>
    public JsonContent(object data, JsonOptions options) {
        this.data = data;
        this.options = options;
        Headers.ContentType = new("application/json");

        BoundedBufferStream bounded = new(bufferLimit);
        Json.Json.Write(data, bounded, options);
        streamed = bounded.Overflowed;
        buffer = bounded.Buffer;
        count = bounded.Count;
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length) {
        length = count;
        return !streamed;
    }

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context) {
        if (streamed) {
            await Json.Json.WriteAsync(data, stream, options);
            return;
        }

        await stream.WriteAsync(buffer, 0, count);
    }
}

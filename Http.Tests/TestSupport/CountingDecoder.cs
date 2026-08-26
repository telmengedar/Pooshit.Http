using System.Net.Http;
using System.Threading.Tasks;
using Pooshit.Http.Encodings;

namespace Http.Tests.TestSupport;

/// <summary>
/// json decoder which counts how often it was asked to decode a response
/// </summary>
public class CountingDecoder : IResponseDecoder {
    readonly IResponseDecoder inner = new JsonDecoder();

    /// <summary>
    /// number of decode calls this decoder received
    /// </summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<T> Decode<T>(HttpResponseMessage message) {
        ++Calls;
        return inner.Decode<T>(message);
    }

    /// <inheritdoc />
    public T DecodeSync<T>(HttpResponseMessage message) {
        ++Calls;
        return inner.DecodeSync<T>(message);
    }
}

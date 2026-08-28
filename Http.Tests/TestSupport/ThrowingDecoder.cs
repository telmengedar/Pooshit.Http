using System;
using System.Net.Http;
using System.Threading.Tasks;
using Pooshit.Http.Encodings;

namespace Http.Tests.TestSupport;

/// <summary>
/// decoder which rejects every response it is asked to decode
/// </summary>
public class ThrowingDecoder : IResponseDecoder {

    /// <inheritdoc />
    public Task<T> Decode<T>(HttpResponseMessage message) => throw new InvalidOperationException("decoder rejected the response");

    /// <inheritdoc />
    public T DecodeSync<T>(HttpResponseMessage message) => throw new InvalidOperationException("decoder rejected the response");
}

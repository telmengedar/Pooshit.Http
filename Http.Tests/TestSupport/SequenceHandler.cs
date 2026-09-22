using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Http.Tests.TestSupport;

/// <summary>
/// handler which returns a pre-configured sequence of responses without touching the network,
/// recording every request it received together with the body it carried
/// </summary>
public class SequenceHandler : HttpMessageHandler {
    readonly Queue<HttpResponseMessage> responses;

    public SequenceHandler(params HttpResponseMessage[] responses) {
        this.responses = new(responses);
    }

    /// <summary>
    /// uri of every request the handler received, in order
    /// </summary>
    public IReadOnlyList<Uri?> RequestedUris => Requests.Select(request => request.RequestUri).ToList();

    /// <summary>
    /// every request the handler received, in order
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// bytes drained from the body of every request the handler received, in order, empty where the request carried no body
    /// </summary>
    public List<byte[]> RequestBodies { get; } = new();

    /// <summary>
    /// whether responses are stamped with the request that produced them, as real handlers do
    /// </summary>
    public bool StampRequestMessage { get; set; } = true;

    static async Task<byte[]> Drain(HttpRequestMessage request) {
        if (request.Content == null)
            return [];

        MemoryStream sink = new();
        try {
            await request.Content.CopyToAsync(sink);
        }
        catch (Exception e) {
            throw new HttpRequestException("An error occurred while sending the request.", e);
        }

        return sink.ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Requests.Add(request);
        RequestBodies.Add(await Drain(request));
        HttpResponseMessage response = responses.Dequeue();
        if (StampRequestMessage)
            response.RequestMessage = request;
        return response;
    }
}

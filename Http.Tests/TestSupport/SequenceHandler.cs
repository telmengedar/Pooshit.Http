using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Http.Tests.TestSupport;

/// <summary>
/// handler which returns a pre-configured sequence of responses without touching the network,
/// recording every request it received
/// </summary>
public class SequenceHandler : HttpMessageHandler {
    readonly Queue<HttpResponseMessage> responses;

    public SequenceHandler(params HttpResponseMessage[] responses) {
        this.responses = new(responses);
    }

    /// <summary>
    /// uri of every request the handler received, in order
    /// </summary>
    public List<Uri?> RequestedUris { get; } = new();

    /// <summary>
    /// every request the handler received, in order
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// whether responses are stamped with the request that produced them, as real handlers do
    /// </summary>
    public bool StampRequestMessage { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        RequestedUris.Add(request.RequestUri);
        Requests.Add(request);
        HttpResponseMessage response = responses.Dequeue();
        // real handlers (SocketsHttpHandler etc.) stamp the originating request onto the
        // response; HttpService relies on RequestMessage.RequestUri to resolve redirects
        if (StampRequestMessage)
            response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

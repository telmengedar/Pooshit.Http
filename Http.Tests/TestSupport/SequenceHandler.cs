using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Http.Tests.TestSupport;

/// <summary>
/// handler which returns a pre-configured sequence of responses without touching the network,
/// recording the uri of every request it received
/// </summary>
public class SequenceHandler : HttpMessageHandler {
    readonly Queue<HttpResponseMessage> responses;

    public SequenceHandler(params HttpResponseMessage[] responses) {
        this.responses = new(responses);
    }

    public List<Uri?> RequestedUris { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        RequestedUris.Add(request.RequestUri);
        HttpResponseMessage response = responses.Dequeue();
        // real handlers (SocketsHttpHandler etc.) stamp the originating request onto the
        // response; HttpService relies on RequestMessage.RequestUri to resolve redirects
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
